# Read-only payload audit. Does not build, install, download, sign or change package registration.
Set-StrictMode -Version Latest

function Get-MdeRequiredPayloadFiles([string]$Architecture) {
    $rid = 'win-' + $Architecture.ToLowerInvariant()
    @('MDEditor.exe', 'MDEditor.dll', 'MDEditor.deps.json', 'MDEditor.runtimeconfig.json',
      'MDEditor.Core.dll', 'MDEditor.Native.dll', 'MDEditor.Typesetting.dll',
      'MDEditor.MathWorker.exe', 'MDEditor.MathWorker.dll', 'MDEditor.MathWorker.deps.json',
      'MDEditor.MathWorker.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
      'System.Private.CoreLib.dll', 'System.Runtime.dll', 'Microsoft.Graphics.Canvas.dll', 'resources.pri',
      'AppxManifest.xml', 'Assets/Tiles/GalleryIcon.ico', 'Assets/Tiles/StoreDisplay-300.png',
      'Assets/Codicons/NOTICE.txt', 'Assets/StarryNight/worker.mjs', 'Assets/StarryNight/onig.wasm',
      'Assets/StarryNight/THIRD-PARTY-LICENSES.txt', 'Assets/StarryNight/GRAMMAR-NOTICES.txt',
      'Assets/StarryNight/NODE-LICENSE.txt', "Assets/StarryNight/runtimes/$rid/node.exe")
    foreach ($weight in @('Black','Bold','Light','Medium','Regular','Thin')) {
        "Fonts/HarmonyOS_SansSC_$weight.ttf"
    }
}

function Get-MdePeArchitecture([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { throw "Invalid PE header: $Path" }
        $stream.Position = 60; $offset = $reader.ReadUInt32()
        if ($offset -lt 64 -or $offset -gt $stream.Length - 6) { throw "Invalid PE offset: $Path" }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE signature: $Path" }
        switch ($reader.ReadUInt16()) {
            0x14C { 'x86' }
            0x8664 { 'x64' }
            0xAA64 { 'arm64' }
            default { throw "Unsupported PE machine: $Path" }
        }
    } finally { $reader.Dispose() }
}

function Resolve-MdePayloadFile([string]$Root, [string]$Relative) {
    $full = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $full.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Payload path escapes its root: $Relative"
    }
    if (-not [IO.File]::Exists($full) -or (Get-Item -LiteralPath $full).Length -eq 0) {
        throw "Payload file missing or empty: $Relative"
    }
    $full
}

function Test-MdeReleasePayload {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path,
          [Parameter(Mandatory)][ValidateSet('x64','x86','ARM64')][string]$Architecture,
          [string]$ReferencePath)
    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    if (-not [IO.Directory]::Exists($root)) { throw "Payload directory does not exist: $root" }
    $architectureName = $Architecture.ToLowerInvariant(); $rid = "win-$architectureName"
    $required = @(Get-MdeRequiredPayloadFiles $Architecture)
    foreach ($relative in $required) { $null = Resolve-MdePayloadFile $root $relative }
    foreach ($relative in @('MDEditor.exe','MDEditor.MathWorker.exe','coreclr.dll','hostfxr.dll',
        'hostpolicy.dll','Microsoft.Graphics.Canvas.dll',"Assets/StarryNight/runtimes/$rid/node.exe")) {
        $actual = Get-MdePeArchitecture (Join-Path $root $relative)
        if ($actual -ne $architectureName) { throw "Architecture mismatch: $relative is $actual, expected $architectureName" }
    }
    $nodes = @(Get-ChildItem -LiteralPath (Join-Path $root 'Assets/StarryNight/runtimes') -Filter node.exe -Recurse -File)
    if ($nodes.Count -ne 1) { throw 'Payload must contain exactly one architecture of Node.' }
    $frameworkVersion = $null
    foreach ($program in @('MDEditor','MDEditor.MathWorker')) {
        $config = Get-Content -LiteralPath (Join-Path $root "$program.runtimeconfig.json") -Raw | ConvertFrom-Json -AsHashtable
        $options = $config.runtimeOptions
        if ($options.ContainsKey('framework') -or $options.ContainsKey('frameworks') -or
            -not $options.ContainsKey('includedFrameworks')) { throw "$program is not self-contained." }
        $frameworks = @($options.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' })
        if ($frameworks.Count -ne 1) { throw "$program has no unambiguous .NET runtime version." }
        if ($null -ne $frameworkVersion -and $frameworkVersion -ne $frameworks[0].version) {
            throw 'UI and MathWorker require different .NET runtime versions.'
        }
        $frameworkVersion = $frameworks[0].version
        $deps = Get-Content -LiteralPath (Join-Path $root "$program.deps.json") -Raw | ConvertFrom-Json -AsHashtable
        if (-not $deps.runtimeTarget.name.EndsWith("/$rid", [StringComparison]::Ordinal)) {
            throw "Dependency RID mismatch: $program"
        }
        $target = $deps.targets[$deps.runtimeTarget.name]
        # These are flattened runtime/native files, not reference-only libraries or another RID's assets.
        foreach ($library in $target.Values) {
            foreach ($kind in @('runtime','native')) {
                if (-not $library.ContainsKey($kind)) { continue }
                foreach ($asset in $library[$kind].Keys) {
                    if ($asset.EndsWith('/_._')) { continue }
                    $null = Resolve-MdePayloadFile $root ([IO.Path]::GetFileName($asset))
                }
            }
        }
    }
    $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = 'Prohibit'; $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create((Join-Path $root 'AppxManifest.xml'), $settings)
    try { $manifest = [Xml.XmlDocument]::new(); $manifest.XmlResolver = $null; $manifest.Load($reader) }
    finally { $reader.Dispose() }
    $identity = $manifest.SelectSingleNode('/*[local-name()="Package"]/*[local-name()="Identity"]')
    if ($null -eq $identity -or $identity.GetAttribute('ProcessorArchitecture') -ne $architectureName) {
        throw 'Manifest architecture does not match the payload.'
    }
    $apps = @($manifest.SelectNodes('//*[local-name()="Applications"]/*[local-name()="Application"]'))
    if ($apps.Count -ne 1 -or $apps[0].GetAttribute('Executable') -ne 'MDEditor.exe') {
        throw 'Only MDEditor.exe should be the user launch entry.'
    }
    foreach ($server in $manifest.SelectNodes('//*[local-name()="InProcessServer"]/*[local-name()="Path"]')) {
        $null = Resolve-MdePayloadFile $root $server.InnerText
    }
    $dependencies = @($manifest.SelectNodes('//*[local-name()="PackageDependency"]') | ForEach-Object {
        [pscustomobject]@{ Name = $_.GetAttribute('Name'); MinVersion = $_.GetAttribute('MinVersion'); Publisher = $_.GetAttribute('Publisher') }
    })
    if ($dependencies.Count -eq 0 -and -not [IO.File]::Exists((Join-Path $root 'Microsoft.UI.Xaml.dll'))) {
        throw 'Neither a Windows App SDK framework dependency nor a local WinUI runtime is present.'
    }
    $checkedHashes = 0
    if ($ReferencePath) {
        $reference = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($ReferencePath))
        foreach ($relative in @('MDEditor.exe','MDEditor.dll','MDEditor.Core.dll','MDEditor.Native.dll',
            'MDEditor.Typesetting.dll','MDEditor.MathWorker.exe','MDEditor.MathWorker.dll',
            'MDEditor.runtimeconfig.json','MDEditor.MathWorker.runtimeconfig.json',
            'MDEditor.MathWorker.deps.json','Assets/StarryNight/worker.mjs','Assets/StarryNight/onig.wasm',
            "Assets/StarryNight/runtimes/$rid/node.exe")) {
            $expected = Resolve-MdePayloadFile $reference $relative
            if ((Get-FileHash -LiteralPath $expected).Hash -ne (Get-FileHash -LiteralPath (Join-Path $root $relative)).Hash) {
                throw "Stale or changed payload file: $relative"
            }
            $checkedHashes++
        }
    }
    [pscustomobject]@{ Path = $root; Architecture = $architectureName; RuntimeVersion = $frameworkVersion
        Identity = $identity.GetAttribute('Name'); Version = $identity.GetAttribute('Version')
        RequiredFiles = $required.Count; ComparedHashes = $checkedHashes; FrameworkDependencies = $dependencies }
}

Export-ModuleMember -Function Get-MdeRequiredPayloadFiles, Get-MdePeArchitecture, Test-MdeReleasePayload
