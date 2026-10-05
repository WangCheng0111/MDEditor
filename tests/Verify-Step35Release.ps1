#Requires -Version 7.2
# Materialize MSBuild's actual file recipe in a disposable GUID directory; never create an installer.
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release',
      [ValidateSet('x64','x86','ARM64')][string[]]$Architectures=@('x64','x86','ARM64'))
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$auditTool=Join-Path $repo 'tools/Verify-ReleasePayload.ps1'
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('mdeditor-release-layout-' + [Guid]::NewGuid().ToString('N'))
$null=[IO.Directory]::CreateDirectory($temporary)
try {
    foreach($architecture in $Architectures) {
        $rid='win-' + $architecture.ToLowerInvariant()
        $output=Join-Path $repo "MDEditor/bin/$architecture/$Configuration/net10.0-windows10.0.19041.0/$rid"
        $recipePath=Join-Path $output 'MDEditor.build.appxrecipe'
        if(-not [IO.File]::Exists($recipePath)){throw "Build $architecture $Configuration first; recipe missing: $recipePath"}
        $settings=[Xml.XmlReaderSettings]::new(); $settings.DtdProcessing='Prohibit'; $settings.XmlResolver=$null
        $reader=[Xml.XmlReader]::Create($recipePath,$settings)
        try {$recipe=[Xml.XmlDocument]::new(); $recipe.XmlResolver=$null; $recipe.Load($reader)} finally {$reader.Dispose()}
        $layout=Join-Path $temporary $architecture; $null=[IO.Directory]::CreateDirectory($layout)
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($item in $recipe.SelectNodes('//*[local-name()="AppxPackagedFile" or local-name()="AppXManifest"]')){
            $source=[regex]::Replace($item.GetAttribute('Include'), '%([0-9a-fA-F]{2})', {param($match) [char][Convert]::ToInt32($match.Groups[1].Value,16)})
            $packagePath=$item.SelectSingleNode('*[local-name()="PackagePath"]').InnerText
            $destination=[IO.Path]::GetFullPath((Join-Path $layout $packagePath))
            if(-not $destination.StartsWith($layout+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){
                throw "Recipe path escapes layout: $packagePath"
            }
            if(-not $seen.Add($destination)){throw "Duplicate recipe destination: $packagePath"}
            if(-not [IO.File]::Exists($source)){throw "Recipe source is missing: $source"}
            $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
            [IO.File]::Copy($source,$destination,$false)
        }
        $hostArchitecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
        $staticOnly=$hostArchitecture -ne $architecture.ToLowerInvariant() -and
            -not ($hostArchitecture -eq 'x64' -and $architecture -eq 'x86')
        & $auditTool -Path $layout -Architecture $architecture -ReferencePath $output -StaticOnly:$staticOnly
    }
} finally {
    $resolved=[IO.Path]::GetFullPath($temporary)
    if([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^mdeditor-release-layout-[0-9a-f]{32}$'){throw 'Unsafe layout cleanup path.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
