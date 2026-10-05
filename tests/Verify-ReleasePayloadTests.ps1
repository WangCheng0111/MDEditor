#Requires -Version 7.2
# Tiny synthetic fixtures only. Never changes a real build, app data or package registration.
$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot 'ReleasePayload.psm1'
if (-not (Test-Path -LiteralPath $module)) { $module = Join-Path $PSScriptRoot '../tools/ReleasePayload.psm1' }
Import-Module $module -Force
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('mdeditor-release-fixture-' + [Guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($fixtureRoot)
$count = 0
function Put([string]$Relative, [string]$Text) {
    $file = Join-Path $fixtureRoot $Relative
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file))
    [IO.File]::WriteAllText($file, $Text)
}
function Pe([string]$Relative, [uint16]$Machine) {
    $bytes = [byte[]]::new(128); $bytes[0] = 0x4D; $bytes[1] = 0x5A; $bytes[60] = 64
    $bytes[64] = 0x50; $bytes[65] = 0x45
    [BitConverter]::GetBytes($Machine).CopyTo($bytes, 68)
    [IO.File]::WriteAllBytes((Join-Path $fixtureRoot $Relative), $bytes)
}
function Make([string]$Architecture) {
    $rid = 'win-' + $Architecture.ToLowerInvariant()
    foreach ($file in Get-MdeRequiredPayloadFiles $Architecture) { Put $file 'fixture' }
    $machine = switch ($Architecture) { 'x64' { 0x8664 } 'x86' { 0x14C } 'arm64' { 0xAA64 } }
    foreach ($file in @('MDEditor.exe','MDEditor.MathWorker.exe','coreclr.dll','hostfxr.dll','hostpolicy.dll',
        'Microsoft.Graphics.Canvas.dll',"Assets/StarryNight/runtimes/$rid/node.exe")) { Pe $file $machine }
    foreach ($program in @('MDEditor','MDEditor.MathWorker')) {
        Put "$program.runtimeconfig.json" '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.11"}]}}'
        Put "$program.deps.json" (@{runtimeTarget=@{name="net10/$rid"}; targets=@{"net10/$rid"=@{
            fixture=@{runtime=@{'System.Runtime.dll'=@{}}}}}} | ConvertTo-Json -Depth 8 -Compress)
    }
    Put 'AppxManifest.xml' "<Package><Identity Name='fixture' Version='1.0.0.0' ProcessorArchitecture='$Architecture'/><Applications><Application Executable='MDEditor.exe'/></Applications><Dependencies><PackageDependency Name='Microsoft.WindowsAppRuntime.2' MinVersion='2.4.0.0' Publisher='fixture'/></Dependencies></Package>"
}
function Bad([string]$Relative, [scriptblock]$Change, [string]$Expected) {
    $file = Join-Path $fixtureRoot $Relative; $bytes = [IO.File]::ReadAllBytes($file)
    try {
        & $Change
        $failure = $null
        try { $null = Test-MdeReleasePayload $fixtureRoot x64 } catch { $failure = $_.Exception.Message }
        if (-not $failure -or $failure -notlike "*$Expected*") { throw "Expected '$Expected', got '$failure'" }
        $script:count++
    } finally { [IO.File]::WriteAllBytes($file, $bytes) }
}
try {
    foreach ($architecture in @('x64','x86','arm64')) {
        Make $architecture
        foreach ($other in @('x64','x86','arm64') | Where-Object { $_ -ne $architecture }) {
            $file = Join-Path $fixtureRoot "Assets/StarryNight/runtimes/win-$other/node.exe"
            if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
        }
        $null = Test-MdeReleasePayload $fixtureRoot $architecture; $count++
    }
    Make 'x64'; [IO.File]::Delete((Join-Path $fixtureRoot 'Assets/StarryNight/runtimes/win-arm64/node.exe'))
    Bad 'MDEditor.MathWorker.dll' { [IO.File]::Delete((Join-Path $fixtureRoot 'MDEditor.MathWorker.dll')) } 'missing or empty'
    Bad 'coreclr.dll' { Pe 'coreclr.dll' 0x14C } 'Architecture mismatch'
    Bad 'MDEditor.exe' { Put 'MDEditor.exe' 'not a PE' } 'Invalid PE'
    Bad 'MDEditor.MathWorker.runtimeconfig.json' { Put 'MDEditor.MathWorker.runtimeconfig.json' '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"10.0.11"}}}' } 'not self-contained'
    Bad 'MDEditor.MathWorker.runtimeconfig.json' { Put 'MDEditor.MathWorker.runtimeconfig.json' '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.12"}]}}' } 'different .NET runtime versions'
    Bad 'MDEditor.deps.json' { Put 'MDEditor.deps.json' '{"runtimeTarget":{"name":"net10/win-x86"},"targets":{}}' } 'Dependency RID mismatch'
    Bad 'AppxManifest.xml' { Put 'AppxManifest.xml' '<Package><Identity ProcessorArchitecture="x86"/></Package>' } 'Manifest architecture'
    Bad 'AppxManifest.xml' { Put 'AppxManifest.xml' '<Package><Identity ProcessorArchitecture="x64"/><Applications><Application Executable="MDEditor.exe"/></Applications><Extensions><InProcessServer><Path>../outside.dll</Path></InProcessServer></Extensions></Package>' } 'escapes its root'
    $reference = Join-Path $fixtureRoot 'reference'
    $null = [IO.Directory]::CreateDirectory($reference)
    foreach ($item in @(Get-ChildItem -LiteralPath $fixtureRoot | Where-Object { $_.Name -ne 'reference' })) {
        Copy-Item -LiteralPath $item.FullName -Destination $reference -Recurse
    }
    Put 'MDEditor.Core.dll' 'stale assembly'
    $failure = $null
    try { $null = Test-MdeReleasePayload $fixtureRoot x64 -ReferencePath $reference } catch { $failure = $_.Exception.Message }
    if ($failure -notlike '*Stale or changed*') { throw "Stale content was not rejected: $failure" }; $count++
    if ($count -ne 12) { throw "Unexpected fixture count: $count" }
    "Release payload fixtures passed: $count"
} finally {
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^mdeditor-release-fixture-[0-9a-f]{32}$') { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
