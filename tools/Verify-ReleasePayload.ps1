#Requires -Version 7.2
# No MSIX generation, installation, registration or network access.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path,
      [Parameter(Mandatory)][ValidateSet('x64','x86','ARM64')][string]$Architecture,
      [string]$ReferencePath, [switch]$StaticOnly, [string]$ReportPath)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleasePayload.psm1') -Force
$audit = Test-MdeReleasePayload -Path $Path -Architecture $Architecture -ReferencePath $ReferencePath
$hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if (-not $StaticOnly -and $hostArchitecture -ne $audit.Architecture -and
    -not ($hostArchitecture -eq 'x64' -and $audit.Architecture -eq 'x86')) {
    throw 'Runtime checks require a compatible machine; use -StaticOnly explicitly for a cross-build.'
}
function Read-Reply($Process, [string]$Stage) {
    $task = $Process.StandardOutput.ReadLineAsync()
    if (-not $task.Wait(15000) -or [string]::IsNullOrWhiteSpace($task.Result)) { throw "No valid reply during $Stage" }
    $task.Result | ConvertFrom-Json -AsHashtable
}
function Test-Child([string]$Executable, [string[]]$Arguments, [scriptblock]$Check) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    # Deliberately remove external Node/.NET search paths. Do not change the machine's environment.
    $start.Environment.Remove('NODE_OPTIONS') | Out-Null; $start.Environment.Remove('NODE_PATH') | Out-Null
    $start.Environment['DOTNET_ROOT'] = Join-Path $audit.Path '__no_external_runtime__'
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $start.Environment['PATH'] = Join-Path $env:windir 'System32'
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    $started = $false
    try {
        if (-not $process.Start()) { throw "Could not start $Executable" }
        $started = $true
        $stderr = $process.StandardError.ReadToEndAsync()
        & $Check $process
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(5000)) { throw "Child did not exit after EOF: $Executable" }
        if ($process.ExitCode -ne 0) { throw "Child exited $($process.ExitCode): $($stderr.GetAwaiter().GetResult())" }
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }
        $process.Dispose()
    }
}
$runtimeChecks = @()
if (-not $StaticOnly) {
    Test-Child (Join-Path $audit.Path 'MDEditor.MathWorker.exe') @('--server') {
        param($process)
        $process.StandardInput.WriteLine('{"messageType":"hello","minimumProtocolVersion":1,"maximumProtocolVersion":1,"transportVersion":1,"clientId":"release-verifier"}')
        $ready = Read-Reply $process 'math handshake'
        if (-not $ready.success -or $ready.selectedProtocolVersion -ne 1) { throw 'Math handshake failed.' }
        foreach ($case in @(@{id='good'; source='\frac{x+1}{\sqrt{y+1}}'; success=$true},
            @{id='invalid'; source='\sqrt{x'; success=$false}, @{id='recovery'; source='\sum_{i=1}^{n}\frac{1}{i^2}'; success=$true})) {
            $request = @{protocolVersion=1; requestId=$case.id; source=$case.source; style='display'; emSize=32; fontFamily='Cambria Math'}
            $process.StandardInput.WriteLine(($request | ConvertTo-Json -Compress))
            $reply = Read-Reply $process $case.id
            if ($reply.success -ne $case.success -or $reply.sessionId -ne $ready.sessionId -or $reply.requestId -ne $case.id) {
                throw "Math response mismatch: $($case.id)"
            }
        }
    }
    $runtimeChecks += 'MathWorker: handshake, formula, invalid input, recovery, clean exit'
    $rid = 'win-' + $audit.Architecture
    Test-Child (Join-Path $audit.Path "Assets/StarryNight/runtimes/$rid/node.exe") @(
        '--max-old-space-size=192', '--disable-proto=delete', (Join-Path $audit.Path 'Assets/StarryNight/worker.mjs')) {
        param($process)
        $ready = Read-Reply $process 'starry-night ready'
        if ($ready.engine -ne 'starry-night' -or $ready.protocol -ne 1 -or $ready.scopes -lt 600) { throw 'Tokenizer handshake failed.' }
        $id = 0
        foreach ($language in @('cs','python','unknown-release-language')) {
            $id++; $text = if ($language -eq 'python') { "for i in range(3):`n    print('中文😀')" } else { 'int answer = 42; // 中文😀' }
            $process.StandardInput.WriteLine((@{id=$id; blocks=@(@{id=0; language=$language; text=$text})} | ConvertTo-Json -Depth 5 -Compress))
            $reply = Read-Reply $process 'highlight'
            if ($reply.ContainsKey('error') -or $reply.id -ne $id -or $reply.blocks.Count -ne 1) { throw 'Tokenizer response failed.' }
            $tokens = @($reply.blocks[0].tokens); $end = 0
            if (($language -eq 'unknown-release-language') -ne ($tokens.Count -eq 0)) { throw 'Tokenizer language fallback failed.' }
            foreach ($token in $tokens) {
                if ($token.start -lt $end -or $token.length -le 0 -or $token.start + $token.length -gt $text.Length) {
                    throw "Tokenizer source range is invalid: $language, start=$($token.start), length=$($token.length), previous=$end, text=$($text.Length)"
                }
                $end = $token.start + $token.length
            }
        }
    }
    $runtimeChecks += 'starry-night: C#, Python, Unicode ranges, unknown language, clean exit'
}
$result = [ordered]@{ Timestamp=[DateTimeOffset]::Now.ToString('o'); Payload=$audit; HostArchitecture=$hostArchitecture
    StaticOnly=[bool]$StaticOnly; RuntimeChecks=$runtimeChecks
    CleanMachineInstallation='Not tested: this command does not create or install an MSIX.' }
if ($ReportPath) { $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ([IO.Path]::GetFullPath($ReportPath)) -Encoding utf8 }
$result | ConvertTo-Json -Depth 10
