#requires -Version 5.0
<#
.SYNOPSIS
    Validate the VerseOps XrmToolBox plugin package before publishing.

.DESCRIPTION
    The XrmToolBox portal reports several package-shape failures as the same
    "XrmToolBox version dependency is missing in Nuget package" error. This
    script checks the final .nupkg directly so CI fails before publishing a
    package the portal will reject.
#>
param(
    [string] $NupkgPath,

    [string] $PackageOutputDir = './artifacts',

    [string] $PackageId = 'VerseOps.XrmToolBox',

    [string] $ExpectedXrmToolBoxVersion = '1.2025.10.74'
)

$ErrorActionPreference = 'Stop'

if (-not $NupkgPath) {
    if (-not (Test-Path $PackageOutputDir)) {
        throw "PackageOutputDir does not exist: $PackageOutputDir"
    }

    $NupkgPath = Get-ChildItem -Path $PackageOutputDir -Filter "$PackageId.*.nupkg" -File |
        Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if (-not $NupkgPath) {
    throw "No $PackageId.*.nupkg found under $PackageOutputDir"
}

if (-not (Test-Path $NupkgPath)) {
    throw "NupkgPath does not exist: $NupkgPath"
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-ZipText {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Compression.ZipArchiveEntry] $Entry
    )

    $reader = New-Object System.IO.StreamReader($Entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Assert-Condition {
    param(
        [bool] $Condition,
        [string] $Message
    )

    if (-not $Condition) { throw $Message }
}

Write-Host "ValidateXrmToolBoxNupkg: opening $NupkgPath"

$zip = [System.IO.Compression.ZipFile]::OpenRead($NupkgPath)
$tempDllPath = $null
try {
    $entries = @($zip.Entries | ForEach-Object { $_.FullName })
    $invalidEntryNames = @($entries | Where-Object { $_ -match '^[/\\]' -or $_ -match '//' -or $_ -match '\\\\' })
    Assert-Condition ($invalidEntryNames.Count -eq 0) "nupkg contains invalid package entry name(s): $($invalidEntryNames -join ', ')"

    $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' -and $_.FullName -notlike '*/*' } | Select-Object -First 1
    Assert-Condition ($null -ne $nuspecEntry) "No top-level .nuspec entry found inside $NupkgPath"

    [xml] $nuspec = Get-ZipText -Entry $nuspecEntry
    $ns = $nuspec.DocumentElement.NamespaceURI
    $nsMgr = New-Object System.Xml.XmlNamespaceManager($nuspec.NameTable)
    $nsMgr.AddNamespace('n', $ns)

    $metadata = $nuspec.SelectSingleNode('/n:package/n:metadata', $nsMgr)
    Assert-Condition ($null -ne $metadata) 'nuspec has no <metadata> element'

    $packageVersion = $metadata.SelectSingleNode('n:version', $nsMgr).InnerText
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($packageVersion)) 'nuspec has no package version'

    $deps = $metadata.SelectSingleNode('n:dependencies', $nsMgr)
    Assert-Condition ($null -ne $deps) 'nuspec has no <dependencies> element'

    $emptyGroups = @($deps.SelectNodes('n:group[not(n:dependency)]', $nsMgr))
    Assert-Condition ($emptyGroups.Count -eq 0) "nuspec contains empty dependency group(s); these can hide loose dependency markers"

    $groups = @($deps.SelectNodes('n:group', $nsMgr))
    $looseDeps = @($deps.SelectNodes('n:dependency', $nsMgr))
    $allDeps = @($deps.SelectNodes('descendant::n:dependency', $nsMgr))
    Assert-Condition ($allDeps.Count -gt 0) 'nuspec dependencies block is empty'

    $unexpectedDeps = @($allDeps | Where-Object { $_.GetAttribute('id') -ne 'XrmToolBox' })
    Assert-Condition ($unexpectedDeps.Count -eq 0) "nuspec has runtime dependencies that the XrmToolBox store will not restore: $((@($unexpectedDeps | ForEach-Object { $_.GetAttribute('id') }) -join ', '))"

    if ($groups.Count -gt 0) {
        Assert-Condition ($looseDeps.Count -eq 0) 'nuspec mixes grouped dependencies with loose dependency entries; loose entries are ignored by NuGet readers'
        foreach ($group in $groups) {
            $marker = $group.SelectSingleNode("n:dependency[@id='XrmToolBox']", $nsMgr)
            Assert-Condition ($null -ne $marker) "dependency group targetFramework='$($group.GetAttribute('targetFramework'))' is missing XrmToolBox marker"
            $actual = $marker.GetAttribute('version')
            Assert-Condition ($actual -eq $ExpectedXrmToolBoxVersion) "XrmToolBox marker in group targetFramework='$($group.GetAttribute('targetFramework'))' has version '$actual'; expected '$ExpectedXrmToolBoxVersion'"
        }
    } else {
        $markers = @($looseDeps | Where-Object { $_.GetAttribute('id') -eq 'XrmToolBox' })
        Assert-Condition ($markers.Count -eq 1) "nuspec must contain exactly one flat XrmToolBox dependency marker; found $($markers.Count)"
        $actual = $markers[0].GetAttribute('version')
        Assert-Condition ($actual -eq $ExpectedXrmToolBoxVersion) "XrmToolBox marker has version '$actual'; expected '$ExpectedXrmToolBoxVersion'"
    }

    $contentTypesEntry = $zip.Entries | Where-Object { $_.FullName -eq '[Content_Types].xml' } | Select-Object -First 1
    Assert-Condition ($null -ne $contentTypesEntry) 'Package is missing [Content_Types].xml'
    [xml] $contentTypes = Get-ZipText -Entry $contentTypesEntry
    $ctNs = $contentTypes.DocumentElement.NamespaceURI
    $ctNsMgr = New-Object System.Xml.XmlNamespaceManager($contentTypes.NameTable)
    $ctNsMgr.AddNamespace('c', $ctNs)
    $pngDefault = $contentTypes.SelectSingleNode("/c:Types/c:Default[@Extension='png']", $ctNsMgr)
    Assert-Condition ($null -ne $pngDefault) '[Content_Types].xml has no PNG default content type'
    Assert-Condition ($pngDefault.GetAttribute('ContentType') -eq 'image/png') "PNG content type is '$($pngDefault.GetAttribute('ContentType'))'; expected 'image/png'"

    $invalidPartNames = @($contentTypes.SelectNodes('/c:Types/c:Override', $ctNsMgr) | Where-Object {
        $partName = $_.GetAttribute('PartName')
        $partName -match '^//' -or $partName -notmatch '^/'
    } | ForEach-Object { $_.GetAttribute('PartName') })
    Assert-Condition ($invalidPartNames.Count -eq 0) "[Content_Types].xml contains invalid Override PartName(s): $($invalidPartNames -join ', ')"

    $requiredEntries = @(
        'lib/net48/Plugins/VerseOps.XrmToolBox.dll',
        'lib/net48/Plugins/VerseOps.XrmToolBox/VerseOps.Api.Core.dll',
        'lib/net48/Plugins/VerseOps.XrmToolBox/Microsoft.Identity.Client.dll',
        'lib/net48/Plugins/VerseOps.XrmToolBox/Microsoft.Identity.Client.Extensions.Msal.dll',
        'lib/net48/Plugins/VerseOps.XrmToolBox/Microsoft.IdentityModel.Abstractions.dll'
    )

    $missingEntries = @($requiredEntries | Where-Object { $_ -notin $entries })
    Assert-Condition ($missingEntries.Count -eq 0) "nupkg is missing required entries: $($missingEntries -join ', ')"

    $libEntries = @($entries | Where-Object { $_ -like 'lib/net48/*' })
    $rootPluginDlls = @($libEntries | Where-Object { $_ -match '^lib/net48/Plugins/[^/]+\.dll$' })
    Assert-Condition ($rootPluginDlls.Count -eq 1 -and $rootPluginDlls[0] -eq 'lib/net48/Plugins/VerseOps.XrmToolBox.dll') "nupkg must have exactly one root plugin DLL at lib/net48/Plugins/VerseOps.XrmToolBox.dll; found: $($rootPluginDlls -join ', ')"

    $bareLibEntries = @($libEntries | Where-Object { $_ -notlike 'lib/net48/Plugins/*' -and $_ -ne 'lib/net48/Plugins/' })
    Assert-Condition ($bareLibEntries.Count -eq 0) "nupkg has entries at bare lib/net48/; all runtime files must be under lib/net48/Plugins/: $($bareLibEntries -join ', ')"

    $pluginEntry = $zip.Entries | Where-Object { $_.FullName -eq 'lib/net48/Plugins/VerseOps.XrmToolBox.dll' } | Select-Object -First 1
    Assert-Condition ($null -ne $pluginEntry) 'Package is missing primary plugin assembly'
    $tempDllPath = Join-Path ([System.IO.Path]::GetTempPath()) ("VerseOps.XrmToolBox.$([System.Guid]::NewGuid().ToString('N')).dll")
    $dllStream = $pluginEntry.Open()
    $fileStream = [System.IO.File]::Create($tempDllPath)
    try { $dllStream.CopyTo($fileStream) } finally { $fileStream.Dispose(); $dllStream.Dispose() }

    $assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($tempDllPath).Version.ToString()
    Assert-Condition ($assemblyVersion -eq $packageVersion) "Plugin assembly version '$assemblyVersion' does not match nuspec version '$packageVersion'"

    Write-Host "ValidateXrmToolBoxNupkg: OK - package version $packageVersion, XrmToolBox marker $ExpectedXrmToolBoxVersion, layout valid"
}
finally {
    if ($zip) { $zip.Dispose() }
    if ($tempDllPath -and (Test-Path $tempDllPath)) { Remove-Item $tempDllPath -Force }
}