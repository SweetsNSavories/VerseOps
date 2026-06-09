param(
    [string]$LogPath = "c:\Users\pravth\Downloads\VerseOps\VerseOps\bap-verify.log"
)

# dotnet test console writes ONE block per test row:
#   "  Skipped <fqn>(...) [Xms]"  or  "  Passed <fqn>(...) [Xms]"  or  "  Failed ..."
# followed (for non-pass) by:
#   "  Error Message:"
#   "   <reason text>"

$content = Get-Content $LogPath -Raw

$rx = [regex]@'
(?ms)^\s+(?<oc>Passed|Skipped|Failed)\s+VerseOps\.SdkTests\.PpacRestCatalogCoverageTests\.Rest_Op_Matrix\(label:\s*"(?<lbl>[^"\r\n]+?)"\S*,\s*op:\s*ApiOperation\s*\{.*?HttpMethod\s*=\s*(?<verb>[A-Z]+),\s*UrlTemplate\s*=\s*(?<url>https?://[^,]+?),\s*TokenScope.*?Surface\s*=\s*(?<sf>Bap|Ppac).*?\}\)\s*\[[^\]]+\]\s*\r?\n(?:\s+Error Message:\s*\r?\n\s+(?<reason>[^\r\n]+))?
'@

$matches = $rx.Matches($content)
Write-Host ("matched rows: {0}" -f $matches.Count)

$rows = foreach ($m in $matches) {
    [pscustomobject]@{
        Surface = $m.Groups['sf'].Value
        Verb    = $m.Groups['verb'].Value
        Url     = $m.Groups['url'].Value.Trim()
        Outcome = $m.Groups['oc'].Value.ToUpper()
        Label   = $m.Groups['lbl'].Value
        Reason  = $m.Groups['reason'].Value.Trim()
    }
}

$uniq = $rows | Sort-Object Surface, Verb, Url -Unique
Write-Host ("unique rows: {0}" -f $uniq.Count)
Write-Host "by surface:"
$uniq | Group-Object Surface | ForEach-Object { Write-Host ("  {0,-5}: {1}" -f $_.Name, $_.Count) }

$bap = $uniq | Where-Object Surface -eq 'Bap'
Write-Host ""
Write-Host ("=== BAP outcomes ({0} unique rows) ===" -f $bap.Count)
$bap | Group-Object Outcome | ForEach-Object { Write-Host ("  {0,-7}: {1}" -f $_.Name, $_.Count) }

Write-Host ""
Write-Host "=== BAP detail (outcome | verb | url | reason) ==="
$bap | Sort-Object Outcome, Verb, Url | ForEach-Object {
    $r = if ($_.Reason) { $_.Reason } else { '(passed)' }
    if ($r.Length -gt 110) { $r = $r.Substring(0,110) + '...' }
    "{0,-7} {1,-6} {2,-100} {3}" -f $_.Outcome, $_.Verb, $_.Url, $r
}

$bap | Sort-Object Outcome, Verb, Url | Export-Csv -Path "c:\Users\pravth\Downloads\VerseOps\VerseOps\bap-verify.csv" -NoTypeInformation
Write-Host ""
Write-Host "CSV written: bap-verify.csv"
