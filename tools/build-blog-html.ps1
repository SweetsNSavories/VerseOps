# tools/build-blog-html.ps1
#
# Renders docs/blog/power-platform-tenant-inventory-in-60-seconds.md to a
# single self-contained HTML file (same conversion pipeline as
# build-blog-pdf.ps1, but skips the headless-Edge print step).
#
# Pipeline:
#   1. Read the markdown.
#   2. Inline-base64 every local image so the HTML is portable.
#   3. Convert markdown -> HTML with the same in-house renderer used for
#      the PDF build (h1-h6, paragraphs, bold/italic, inline code, links,
#      images, lists, blockquotes, hr, fenced code incl. mermaid, tables).
#   4. Wrap in print/web-friendly CSS and load mermaid.min.js from CDN.
#   5. Write to docs/blog/<name>.html.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\tools\build-blog-html.ps1
#
# Output: docs/blog/power-platform-tenant-inventory-in-60-seconds.html

[CmdletBinding()]
param(
    [string] $InMd,
    [string] $OutHtml
)

$ErrorActionPreference = 'Stop'

$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $InMd)    { $InMd    = Join-Path $scriptRoot '..\docs\blog\power-platform-tenant-inventory-in-60-seconds.md' }
if (-not $OutHtml) { $OutHtml = Join-Path $scriptRoot '..\docs\blog\power-platform-tenant-inventory-in-60-seconds.html' }
$InMd    = (Resolve-Path $InMd).Path
$OutHtml = [IO.Path]::GetFullPath($OutHtml)
if (-not (Test-Path $InMd)) { throw "Markdown not found at $InMd" }

$mdDir = Split-Path -Parent (Resolve-Path $InMd).Path
$md    = [IO.File]::ReadAllText($InMd, [Text.Encoding]::UTF8)

# ---------------------------------------------------------------------
# 1) Inline images. Convert ![alt](path) -> ![alt](data:image/...;base64,...)
# ---------------------------------------------------------------------
$md = [regex]::Replace($md, '!\[(?<alt>[^\]]*)\]\((?<src>[^)\s]+)\)', {
    param($m)
    $src = $m.Groups['src'].Value
    if ($src -match '^https?://' -or $src -match '^data:') { return $m.Value }
    $abs = Join-Path $mdDir $src
    if (-not (Test-Path $abs)) { Write-Warning "missing image: $abs"; return $m.Value }
    $bytes = [IO.File]::ReadAllBytes($abs)
    $b64   = [Convert]::ToBase64String($bytes)
    $ext   = [IO.Path]::GetExtension($abs).TrimStart('.').ToLower()
    if ($ext -eq 'jpg') { $ext = 'jpeg' }
    return "![$($m.Groups['alt'].Value)](data:image/$ext;base64,$b64)"
})

# ---------------------------------------------------------------------
# 2) Markdown -> HTML (same hand-rolled renderer as build-blog-pdf.ps1).
# ---------------------------------------------------------------------
function Convert-MarkdownToHtml {
    param([string] $text)

    $sb = New-Object System.Text.StringBuilder
    $lines = $text -split "`r?`n"
    $i = 0
    $inList = $false
    $listType = $null

    function FlushList { param([System.Text.StringBuilder] $b, [ref] $inListRef, [ref] $listTypeRef)
        if ($inListRef.Value) { [void]$b.AppendLine("</$($listTypeRef.Value)>"); $inListRef.Value = $false; $listTypeRef.Value = $null }
    }

    function Inline {
        param([string] $s)
        $s = $s -replace '&', '&amp;'
        $s = $s -replace '<', '&lt;'
        $s = $s -replace '>', '&gt;'
        $s = [regex]::Replace($s, '!\[([^\]]*)\]\(([^)]+)\)', {
            param($m)
            $alt = $m.Groups[1].Value -replace '"', '&quot;'
            $src = $m.Groups[2].Value
            "<img alt=`"$alt`" src=`"$src`" />"
        })
        $s = [regex]::Replace($s, '\[([^\]]+)\]\(([^)]+)\)', '<a href="$2">$1</a>')
        $s = [regex]::Replace($s, '\*\*([^*]+)\*\*', '<strong>$1</strong>')
        $s = [regex]::Replace($s, '(?<!\*)\*(?!\*)([^*]+)(?<!\*)\*(?!\*)', '<em>$1</em>')
        $s = [regex]::Replace($s, '`([^`]+)`', '<code>$1</code>')
        return $s
    }

    while ($i -lt $lines.Count) {
        $line = $lines[$i]

        if ($line -match '^```(?<lang>\w*)\s*$') {
            FlushList $sb ([ref]$inList) ([ref]$listType)
            $lang = $Matches['lang']
            $i++
            $buf = New-Object System.Text.StringBuilder
            while ($i -lt $lines.Count -and $lines[$i] -notmatch '^```\s*$') {
                [void]$buf.AppendLine($lines[$i]); $i++
            }
            $i++
            $code = $buf.ToString().TrimEnd()
            if ($lang -eq 'mermaid') {
                [void]$sb.AppendLine("<div class=`"mermaid`">$code</div>")
            } else {
                $escaped = $code -replace '&', '&amp;' -replace '<', '&lt;' -replace '>', '&gt;'
                [void]$sb.AppendLine("<pre><code class=`"language-$lang`">$escaped</code></pre>")
            }
            continue
        }

        if ($line -match '^\s*\|.+\|\s*$' -and ($i + 1) -lt $lines.Count -and $lines[$i + 1] -match '^\s*\|[\s\|:-]+\|\s*$') {
            FlushList $sb ([ref]$inList) ([ref]$listType)
            $headerCells = ($line.Trim() -replace '^\||\|$', '') -split '\|' | ForEach-Object { Inline $_.Trim() }
            $i += 2
            [void]$sb.AppendLine("<table><thead><tr>")
            foreach ($h in $headerCells) { [void]$sb.AppendLine("<th>$h</th>") }
            [void]$sb.AppendLine("</tr></thead><tbody>")
            while ($i -lt $lines.Count -and $lines[$i] -match '^\s*\|.+\|\s*$') {
                $cells = ($lines[$i].Trim() -replace '^\||\|$', '') -split '\|' | ForEach-Object { Inline $_.Trim() }
                [void]$sb.AppendLine("<tr>")
                foreach ($c in $cells) { [void]$sb.AppendLine("<td>$c</td>") }
                [void]$sb.AppendLine("</tr>")
                $i++
            }
            [void]$sb.AppendLine("</tbody></table>")
            continue
        }

        if ($line -match '^\s*$') {
            FlushList $sb ([ref]$inList) ([ref]$listType)
            $i++; continue
        }

        if ($line -match '^(#{1,6})\s+(.+)$') {
            FlushList $sb ([ref]$inList) ([ref]$listType)
            $level = $Matches[1].Length
            $text  = Inline $Matches[2]
            [void]$sb.AppendLine("<h$level>$text</h$level>")
            $i++; continue
        }

        if ($line -match '^---+\s*$') {
            FlushList $sb ([ref]$inList) ([ref]$listType)
            [void]$sb.AppendLine("<hr/>")
            $i++; continue
        }

        if ($line -match '^>\s?(.*)$') {
            FlushList $sb ([ref]$inList) ([ref]$listType)
            $paragraphs = New-Object System.Collections.ArrayList
            $cur = New-Object System.Text.StringBuilder
            while ($i -lt $lines.Count -and $lines[$i] -match '^>\s?(.*)$') {
                $content = $Matches[1]
                if ([string]::IsNullOrWhiteSpace($content)) {
                    if ($cur.Length -gt 0) { [void]$paragraphs.Add($cur.ToString().Trim()); $cur = New-Object System.Text.StringBuilder }
                } else {
                    if ($cur.Length -gt 0) { [void]$cur.Append(' ') }
                    [void]$cur.Append($content)
                }
                $i++
            }
            if ($cur.Length -gt 0) { [void]$paragraphs.Add($cur.ToString().Trim()) }
            $inner = ($paragraphs | ForEach-Object { "<p>$(Inline $_)</p>" }) -join "`n"
            [void]$sb.AppendLine("<blockquote>$inner</blockquote>")
            continue
        }

        if ($line -match '^\s*\d+\.\s+(.+)$') {
            if (-not $inList -or $listType -ne 'ol') {
                FlushList $sb ([ref]$inList) ([ref]$listType)
                [void]$sb.AppendLine("<ol>")
                $inList = $true; $listType = 'ol'
            }
            [void]$sb.AppendLine("<li>$(Inline $Matches[1])</li>")
            $i++; continue
        }

        if ($line -match '^\s*[-*]\s+(.+)$') {
            if (-not $inList -or $listType -ne 'ul') {
                FlushList $sb ([ref]$inList) ([ref]$listType)
                [void]$sb.AppendLine("<ul>")
                $inList = $true; $listType = 'ul'
            }
            [void]$sb.AppendLine("<li>$(Inline $Matches[1])</li>")
            $i++; continue
        }

        FlushList $sb ([ref]$inList) ([ref]$listType)
        $buf = New-Object System.Text.StringBuilder
        while ($i -lt $lines.Count -and $lines[$i] -notmatch '^\s*$' `
                -and $lines[$i] -notmatch '^#{1,6}\s' -and $lines[$i] -notmatch '^---+\s*$' `
                -and $lines[$i] -notmatch '^>\s?' -and $lines[$i] -notmatch '^```' `
                -and $lines[$i] -notmatch '^\s*[-*]\s+' -and $lines[$i] -notmatch '^\s*\d+\.\s+') {
            if ($buf.Length -gt 0) { [void]$buf.Append(' ') }
            [void]$buf.Append((Inline $lines[$i]))
            $i++
        }
        [void]$sb.AppendLine("<p>$($buf.ToString())</p>")
    }
    FlushList $sb ([ref]$inList) ([ref]$listType)
    return $sb.ToString()
}

$bodyHtml = Convert-MarkdownToHtml $md

# ---------------------------------------------------------------------
# 3) Wrap in CSS shared with the PDF build, plus a small web-readable
#    body width so it looks reasonable when opened directly in a browser.
# ---------------------------------------------------------------------
$css = @'
@page { size: Letter; margin: 0.6in 0.55in; }
body { font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; color: #1f1f1f; font-size: 10.5pt; line-height: 1.45; max-width: 7.5in; margin: 24pt auto; padding: 0 16pt; }
h1 { font-size: 22pt; font-weight: 600; color: #0f172a; margin: 0 0 6pt; line-height: 1.15; }
h2 { font-size: 15pt; font-weight: 600; color: #0f172a; margin: 22pt 0 8pt; border-bottom: 1px solid #e5e7eb; padding-bottom: 4pt; page-break-after: avoid; }
h3 { font-size: 12pt; font-weight: 600; color: #1e293b; margin: 16pt 0 6pt; page-break-after: avoid; }
p  { margin: 0 0 8pt; }
em { color: #475569; }
strong { color: #0f172a; }
a  { color: #0f4e9c; text-decoration: none; }
hr { border: none; border-top: 1px solid #e5e7eb; margin: 14pt 0; }
img { max-width: 100%; height: auto; display: block; margin: 10pt auto; border: 1px solid #e5e7eb; border-radius: 4px; page-break-inside: avoid; }
blockquote { background: #f8fafc; border-left: 3px solid #0f4e9c; padding: 6pt 10pt; margin: 10pt 0; color: #1e293b; }
blockquote p { margin: 0 0 4pt; }
table { width: 100%; border-collapse: collapse; margin: 8pt 0 12pt; font-size: 9.5pt; page-break-inside: avoid; }
thead th { text-align: left; background: #f1f5f9; border-bottom: 1px solid #cbd5e1; padding: 5pt 7pt; color: #0f172a; }
tbody td { border-bottom: 1px solid #e5e7eb; padding: 4pt 7pt; vertical-align: top; }
tbody tr:nth-child(even) td { background: #fafbfc; }
code { background: #f1f5f9; padding: 1pt 4pt; border-radius: 3px; font-family: Consolas, 'Cascadia Mono', monospace; font-size: 9pt; color: #0f172a; }
pre { background: #0f172a; color: #e2e8f0; padding: 9pt 12pt; border-radius: 5px; overflow-x: auto; font-family: Consolas, 'Cascadia Mono', monospace; font-size: 8.5pt; line-height: 1.35; page-break-inside: avoid; }
pre code { background: transparent; color: inherit; padding: 0; }
ul, ol { margin: 0 0 10pt 16pt; padding: 0; }
li { margin: 0 0 3pt; }
.mermaid { text-align: center; margin: 12pt 0; page-break-inside: avoid; background: #fff; padding: 8pt; border: 1px solid #e5e7eb; border-radius: 4px; }
.mermaid svg { max-width: 100% !important; height: auto !important; }
'@

$html = @"
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>VerseOps blog</title>
<style>$css</style>
</head>
<body>
$bodyHtml
<script src="https://cdn.jsdelivr.net/npm/mermaid@10/dist/mermaid.min.js"></script>
<script>
  mermaid.initialize({ startOnLoad: true, theme: 'neutral', securityLevel: 'loose', flowchart: { htmlLabels: true, useMaxWidth: true } });
</script>
</body>
</html>
"@

[IO.File]::WriteAllText($OutHtml, $html, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "OK -> $OutHtml ($([math]::Round((Get-Item $OutHtml).Length/1KB,1)) KB)"
