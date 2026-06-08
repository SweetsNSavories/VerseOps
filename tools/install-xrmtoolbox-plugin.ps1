# Smoke install: copy plugin DLL + deps into XrmToolBox global plugins folder.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repo 'VerseOps.XrmToolBox\bin\Debug\net48'
$dst = Join-Path $env:APPDATA 'MscrmTools\XrmToolBox\Plugins\VerseOps'

if (-not (Test-Path $src)) { throw "Build output not found at $src" }
New-Item -ItemType Directory -Force $dst | Out-Null

# Don't ship the host or the tool-library (XrmToolBox provides them).
$skip = @('XrmToolBox.exe','XrmToolBox.exe.config','XrmToolBox.pdb','XrmToolBox.ToolLibrary.dll')
Get-ChildItem $src -File | Where-Object { $skip -notcontains $_.Name } | ForEach-Object {
    Copy-Item $_.FullName -Destination $dst -Force
}
if (Test-Path (Join-Path $src 'runtimes')) {
    Copy-Item (Join-Path $src 'runtimes') $dst -Recurse -Force
}
"Installed $((Get-ChildItem $dst -Recurse -File | Measure-Object).Count) files to $dst"
Get-ChildItem $dst -Filter 'VerseOps.*.dll' | Format-Table Name, Length, LastWriteTime -AutoSize
