#requires -Version 5.0
<#
.SYNOPSIS
    Inject an XrmToolBox marker dependency into the .nuspec inside a .nupkg.

.DESCRIPTION
    The XrmToolBox plugin-store registration page rejects submissions whose
    nuspec dependencies block does not contain
        <dependency id="XrmToolBox" version="1.2017.10.19" />
    with the error "XrmToolBox version dependency is missing in Nuget
    package". The package id "XrmToolBox" does not actually exist on
    nuget.org (404 on flatcontainer); this is purely a metadata signal.

    Our SDK-style csproj sets PrivateAssets="all" on every PackageReference
    so the generated nuspec ships with an empty <dependencies/> element.
    This script opens the .nupkg, locates the .nuspec entry, loads it as
    XML, ensures the marker dependency is present, and saves the modified
    .nuspec back into the .nupkg.

    Idempotent: re-running on an already-fixed nupkg is a no-op.

.PARAMETER NupkgPath
    Absolute path to the .nupkg file to rewrite.

.PARAMETER DepId
    NuGet package id to inject as a dependency. Default: XrmToolBox.

.PARAMETER DepVersion
    Version constraint for the injected dependency. Default: 1.2017.10.19.

.NOTES
    Runs from MSBuild AfterTargets="Pack" in VerseOps.XrmToolBox.csproj.
    Requires PowerShell 5+ and System.IO.Compression.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageOutputDir,

    [Parameter(Mandatory = $true)]
    [string] $PackageId,

    [string] $DepId = 'XrmToolBox',

    [string] $DepVersion = '1.2017.10.19'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PackageOutputDir)) {
    throw "PackageOutputDir does not exist: $PackageOutputDir"
}

# Find the .nupkg the SDK just produced. Exclude .symbols.nupkg (we don't ship
# symbols on this project, but be defensive). NuGet normalizes the version in
# the file name (e.g. 1.0.10.0 -> 1.0.10), so we can't predict the exact name
# from $(PackageVersion); glob and take the newest match.
$NupkgPath = Get-ChildItem -Path $PackageOutputDir -Filter "$PackageId.*.nupkg" -File |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $NupkgPath) {
    throw "No $PackageId.*.nupkg found under $PackageOutputDir"
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

Write-Host "InjectXrmToolBoxDep: opening $NupkgPath"
$zip = [System.IO.Compression.ZipFile]::Open($NupkgPath, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' -and $_.FullName -notlike '*/*' } | Select-Object -First 1
    if (-not $nuspecEntry) {
        throw "No top-level .nuspec entry found inside $NupkgPath"
    }
    Write-Host "InjectXrmToolBoxDep: found nuspec entry $($nuspecEntry.FullName)"

    # Read the nuspec content out
    $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
    try { $nuspecXmlText = $reader.ReadToEnd() } finally { $reader.Dispose() }

    [xml] $doc = $nuspecXmlText
    $ns = $doc.DocumentElement.NamespaceURI
    $nsMgr = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $nsMgr.AddNamespace('n', $ns)

    $metadata = $doc.SelectSingleNode('/n:package/n:metadata', $nsMgr)
    if (-not $metadata) { throw 'nuspec has no <metadata> element' }

    $deps = $doc.SelectSingleNode('/n:package/n:metadata/n:dependencies', $nsMgr)
    if (-not $deps) {
        Write-Host 'InjectXrmToolBoxDep: <dependencies> missing; creating empty element'
        $deps = $doc.CreateElement('dependencies', $ns)
        [void] $metadata.AppendChild($deps)
    }

    # Idempotency: skip if marker already present at any depth (top-level or
    # inside a <group>).
    $existing = $deps.SelectSingleNode("descendant::n:dependency[@id='$DepId']", $nsMgr)
    if ($existing) {
        Write-Host "InjectXrmToolBoxDep: dependency id='$DepId' already present (version='$($existing.GetAttribute('version'))'); no change"
        return
    }

    $dep = $doc.CreateElement('dependency', $ns)
    $dep.SetAttribute('id', $DepId)
    $dep.SetAttribute('version', $DepVersion)
    [void] $deps.AppendChild($dep)
    Write-Host "InjectXrmToolBoxDep: appended <dependency id='$DepId' version='$DepVersion' />"

    # Serialize XML through a MemoryStream so the writer's declared encoding
    # matches the actual byte encoding. StringBuilder forces utf-16 into the
    # XML declaration regardless of XmlWriterSettings.Encoding, which produces
    # a nuspec that's bytes-on-disk UTF-8 but self-declares as utf-16 -
    # NuGet/XTB validators reject (or worse, misread) such files.
    $ms = New-Object System.IO.MemoryStream
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.IndentChars = '  '
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $settings.OmitXmlDeclaration = $false
    $writer = [System.Xml.XmlWriter]::Create($ms, $settings)
    try { $doc.Save($writer) } finally { $writer.Dispose() }
    $bytes = $ms.ToArray()
    $ms.Dispose()

    $stream = $nuspecEntry.Open()
    try {
        $stream.SetLength(0)
        $stream.Write($bytes, 0, $bytes.Length)
    } finally {
        $stream.Dispose()
    }
    Write-Host "InjectXrmToolBoxDep: rewrite complete"
}
finally {
    $zip.Dispose()
}
