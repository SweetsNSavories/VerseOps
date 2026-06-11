#requires -Version 5.0
<#
.SYNOPSIS
    Inject an XrmToolBox marker dependency into the .nuspec inside a .nupkg.

.DESCRIPTION
    The XrmToolBox plugin-store registration page rejects submissions whose
    nuspec dependencies block does not contain an XrmToolBox dependency whose
    version matches the minimum XrmToolBox host version targeted by the tool,
    with the error "XrmToolBox version dependency is missing in Nuget
    package". The package id "XrmToolBox" is a Tool Library compatibility
    signal rather than the SDK package used at build time.

    Our SDK-style csproj sets PrivateAssets="all" on every PackageReference
    so the generated nuspec ships with an empty <dependencies/> element.
    This script opens the .nupkg, locates the .nuspec entry, loads it as
    XML, ensures the marker dependency is present, and saves the modified
    .nuspec back into the .nupkg.

    Idempotent: re-running on an already-fixed nupkg does not duplicate the
    marker. If an older marker version is present, it is updated in place.

.PARAMETER NupkgPath
    Absolute path to the .nupkg file to rewrite.

.PARAMETER DepId
    NuGet package id to inject as a dependency. Default: XrmToolBox.

.PARAMETER DepVersion
    Version constraint for the injected dependency. Default: 1.2025.10.74.

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

    [string] $DepVersion = '1.2025.10.74'
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
    # [Content_Types].xml fix: NuGet's pack writes <Default Extension="png"
    # ContentType="application/octet" />, which makes the nuget.org icon-proxy
    # endpoint (https://api.nuget.org/v3-flatcontainer/<id>/<ver>/icon) serve
    # the embedded PNG with Content-Type: application/octet-stream. The
    # XrmToolBox plugin-store validator reads the iconUrl from the nuget.org
    # search index (which IS that proxy URL when <icon> is embedded) and
    # rejects with "Logo Url is not valid" when the response Content-Type
    # isn't image/*. Rewrite the entry to ContentType="image/png".
    $ctEntry = $zip.Entries | Where-Object { $_.FullName -eq '[Content_Types].xml' } | Select-Object -First 1
    if ($ctEntry) {
        $ctReader = New-Object System.IO.StreamReader($ctEntry.Open())
        try { $ctText = $ctReader.ReadToEnd() } finally { $ctReader.Dispose() }
        [xml] $ctDoc = $ctText
        $ctNs = $ctDoc.DocumentElement.NamespaceURI
        $ctNsMgr = New-Object System.Xml.XmlNamespaceManager($ctDoc.NameTable)
        $ctNsMgr.AddNamespace('c', $ctNs)
        $pngDefault = $ctDoc.SelectSingleNode("/c:Types/c:Default[@Extension='png']", $ctNsMgr)
        if ($pngDefault) {
            $current = $pngDefault.GetAttribute('ContentType')
            if ($current -ne 'image/png') {
                Write-Host "InjectXrmToolBoxDep: rewriting [Content_Types].xml png ContentType '$current' -> 'image/png'"
                $pngDefault.SetAttribute('ContentType', 'image/png')
                $ctMs = New-Object System.IO.MemoryStream
                $ctSettings = New-Object System.Xml.XmlWriterSettings
                $ctSettings.Indent = $true
                $ctSettings.IndentChars = '  '
                $ctSettings.Encoding = New-Object System.Text.UTF8Encoding($false)
                $ctSettings.OmitXmlDeclaration = $false
                $ctWriter = [System.Xml.XmlWriter]::Create($ctMs, $ctSettings)
                try { $ctDoc.Save($ctWriter) } finally { $ctWriter.Dispose() }
                $ctBytes = $ctMs.ToArray()
                $ctMs.Dispose()
                $ctStream = $ctEntry.Open()
                try {
                    $ctStream.SetLength(0)
                    $ctStream.Write($ctBytes, 0, $ctBytes.Length)
                } finally {
                    $ctStream.Dispose()
                }
                Write-Host "InjectXrmToolBoxDep: [Content_Types].xml rewrite complete"
            }
        }
    }

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

    # NuGet's dependency model is "either grouped or flat, not both": if any
    # <group> child exists, loose top-level <dependency> siblings are ignored
    # by readers (including the XrmToolBox plugin-store validator). The SDK
    # often emits empty per-TFM <group> elements (e.g.
    # <group targetFramework=".NETFramework4.8" />) when PrivateAssets="all"
    # strips every real dep; those empty groups must be removed before we add
    # a flat marker, otherwise the marker is silently swallowed and the
    # validator reports "XrmToolBox version dependency is missing".
    $emptyGroups = @($deps.SelectNodes("n:group[not(n:dependency)]", $nsMgr))
    foreach ($g in $emptyGroups) {
        Write-Host "InjectXrmToolBoxDep: removing empty <group targetFramework='$($g.GetAttribute('targetFramework'))' />"
        [void] $deps.RemoveChild($g)
    }

    # If non-empty <group> elements remain, NuGet readers will still ignore
    # loose siblings - ensure the marker is present inside every remaining
    # group instead. Remove loose marker entries so readers cannot disagree
    # about whether the package uses grouped or flat dependencies.
    $remainingGroups = @($deps.SelectNodes("n:group", $nsMgr))
    if ($remainingGroups.Count -gt 0) {
        $looseMarkers = @($deps.SelectNodes("n:dependency[@id='$DepId']", $nsMgr))
        foreach ($loose in $looseMarkers) {
            Write-Host "InjectXrmToolBoxDep: removing loose <dependency id='$DepId' /> because grouped dependencies are present"
            [void] $deps.RemoveChild($loose)
        }

        foreach ($g in $remainingGroups) {
            $dep = $g.SelectSingleNode("n:dependency[@id='$DepId']", $nsMgr)
            if ($dep) {
                $currentVersion = $dep.GetAttribute('version')
                if ($currentVersion -ne $DepVersion) {
                    Write-Host "InjectXrmToolBoxDep: updating <dependency id='$DepId' /> in group targetFramework='$($g.GetAttribute('targetFramework'))' from version '$currentVersion' to '$DepVersion'"
                    $dep.SetAttribute('version', $DepVersion)
                } else {
                    Write-Host "InjectXrmToolBoxDep: dependency id='$DepId' already present in group targetFramework='$($g.GetAttribute('targetFramework'))' with version '$DepVersion'"
                }
            } else {
                $dep = $doc.CreateElement('dependency', $ns)
                $dep.SetAttribute('id', $DepId)
                $dep.SetAttribute('version', $DepVersion)
                [void] $g.AppendChild($dep)
                Write-Host "InjectXrmToolBoxDep: appended <dependency id='$DepId' version='$DepVersion' /> into group targetFramework='$($g.GetAttribute('targetFramework'))'"
            }
        }
    } else {
        $markerDeps = @($deps.SelectNodes("n:dependency[@id='$DepId']", $nsMgr))
        if ($markerDeps.Count -gt 0) {
            $primary = $markerDeps[0]
            $currentVersion = $primary.GetAttribute('version')
            if ($currentVersion -ne $DepVersion) {
                Write-Host "InjectXrmToolBoxDep: updating flat <dependency id='$DepId' /> from version '$currentVersion' to '$DepVersion'"
                $primary.SetAttribute('version', $DepVersion)
            } else {
                Write-Host "InjectXrmToolBoxDep: flat dependency id='$DepId' already present with version '$DepVersion'"
            }

            if ($markerDeps.Count -gt 1) {
                foreach ($duplicate in $markerDeps[1..($markerDeps.Count - 1)]) {
                    Write-Host "InjectXrmToolBoxDep: removing duplicate flat <dependency id='$DepId' />"
                    [void] $deps.RemoveChild($duplicate)
                }
            }
        } else {
            $dep = $doc.CreateElement('dependency', $ns)
            $dep.SetAttribute('id', $DepId)
            $dep.SetAttribute('version', $DepVersion)
            [void] $deps.AppendChild($dep)
            Write-Host "InjectXrmToolBoxDep: appended flat <dependency id='$DepId' version='$DepVersion' />"
        }
    }

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
