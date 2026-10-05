param(
    [string]$WorkerPath = (Join-Path $PSScriptRoot '..\MDEditor.MathWorker\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\MDEditor.MathWorker.exe'),
    [string]$ReportPath = (Join-Path $PSScriptRoot '..\TestResults\step-13-math-service.json')
)

$ErrorActionPreference = 'Stop'
$worker = [IO.Path]::GetFullPath($WorkerPath)
if (-not [IO.File]::Exists($worker)) { throw "MathWorker is missing: $worker" }

function Read-LineWithTimeout($process, [string]$stage) {
    $read = $process.StandardOutput.ReadLineAsync()
    if (-not $read.Wait(10000)) {
        if (-not $process.HasExited) { $process.Kill($true) }
        throw "MathWorker timed out during $stage."
    }
    $line = $read.Result
    if ([string]::IsNullOrWhiteSpace($line)) { throw "MathWorker closed stdout during $stage." }
    $line | ConvertFrom-Json -Depth 100
}

function Start-Service([int]$minimum = 1, [int]$maximum = 1) {
    $start = [Diagnostics.ProcessStartInfo]::new($worker)
    $start.ArgumentList.Add('--server')
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($worker)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    if (-not $process.Start()) { throw 'Persistent MathWorker did not start.' }
    $stderr = $process.StandardError.ReadToEndAsync()
    $hello = [ordered]@{
        messageType = 'hello'; minimumProtocolVersion = $minimum; maximumProtocolVersion = $maximum
        transportVersion = 1; clientId = 'step-13-verifier'
    } | ConvertTo-Json -Compress
    $process.StandardInput.WriteLine($hello); $process.StandardInput.Flush()
    $ready = Read-LineWithTimeout $process 'handshake'
    [pscustomobject]@{ Process = $process; Ready = $ready; Stderr = $stderr }
}

function Request-Json([string]$id, [string]$source) {
    [ordered]@{
        protocolVersion = 1; requestId = $id; source = $source
        style = 'display'; emSize = 32.0; fontFamily = 'Cambria Math'
    } | ConvertTo-Json -Compress
}

function Send-Request($service, [string]$id, [string]$source) {
    $service.Process.StandardInput.WriteLine((Request-Json $id $source))
    $service.Process.StandardInput.Flush()
    Read-LineWithTimeout $service.Process $id
}

function Stop-Service($service) {
    $service.Process.StandardInput.Close()
    if (-not $service.Process.WaitForExit(5000)) {
        $service.Process.Kill($true); throw 'Persistent MathWorker did not exit after stdin closed.'
    }
    $exitCode = $service.Process.ExitCode
    $stderr = if ($service.Stderr.Wait(1000)) { $service.Stderr.Result } else { '' }
    $service.Process.Dispose()
    if ($exitCode -ne 0) { throw "Persistent MathWorker exited with $exitCode. $stderr" }
}

$first = Start-Service
if (-not $first.Ready.success -or $first.Ready.selectedProtocolVersion -ne 1 -or
    $first.Ready.processId -ne $first.Process.Id -or [string]::IsNullOrWhiteSpace($first.Ready.sessionId) -or
    $first.Ready.cacheCapacity -ne 128) { throw 'Persistent handshake metadata is invalid.' }

$cases = @(
    @{ Id = 'persistent-a'; Source = '\frac{x_i^2+1}{\sqrt{y+1}}' },
    @{ Id = 'persistent-b'; Source = '\sqrt{\frac{1}{\frac{1}{\frac{1}{1}}}}' },
    @{ Id = 'persistent-c'; Source = '\sum_{i=1}^{n}\frac{1}{i^2}' }
)
foreach ($case in $cases) { $first.Process.StandardInput.WriteLine((Request-Json $case.Id $case.Source)) }
$first.Process.StandardInput.Flush()
$responses = @($cases | ForEach-Object { Read-LineWithTimeout $first.Process $_.Id })
if (@($responses | Where-Object { -not $_.success -or $null -eq $_.layout }).Count -ne 0) {
    throw 'A persistent layout request failed.'
}
if (@($responses.sessionId | Sort-Object -Unique).Count -ne 1 -or $responses[0].sessionId -ne $first.Ready.sessionId) {
    throw 'Requests did not stay inside the negotiated session.'
}
if (($responses.sequence -join ',') -ne '1,2,3') { throw 'Persistent response sequence is not monotonic.' }

$cached = Send-Request $first 'persistent-cache' $cases[0].Source
if (-not $cached.success -or -not $cached.cacheHit -or $cached.sequence -ne 4) {
    throw 'Formula cache hit was not observed.'
}
$invalid = Send-Request $first 'persistent-invalid' '\sqrt{x'
if ($invalid.success -or $invalid.errorCode -ne 'invalid-formula' -or $first.Process.HasExited) {
    throw 'Invalid formula did not remain a structured, non-fatal session error.'
}
$afterError = Send-Request $first 'persistent-after-error' $cases[1].Source
if (-not $afterError.success -or -not $afterError.cacheHit -or $afterError.sessionId -ne $first.Ready.sessionId) {
    throw 'The persistent service was not usable after an invalid formula.'
}

$firstProcessId = $first.Process.Id; $firstSessionId = $first.Ready.sessionId
$first.Process.Kill($true); $null = $first.Process.WaitForExit(5000); $first.Process.Dispose()
$second = Start-Service
if (-not $second.Ready.success -or $second.Process.Id -eq $firstProcessId -or $second.Ready.sessionId -eq $firstSessionId) {
    throw 'Replacement service did not create a new process and session.'
}
$recovered = Send-Request $second 'persistent-recovered' $cases[0].Source
if (-not $recovered.success -or $recovered.cacheHit -or $recovered.sequence -ne 1) {
    throw 'Replacement service did not start with a clean cache and sequence.'
}
Stop-Service $second

$incompatible = Start-Service 2 2
if ($incompatible.Ready.success -or $incompatible.Ready.errorCode -ne 'protocol-mismatch') {
    throw 'An incompatible protocol range was not rejected.'
}
if (-not $incompatible.Process.WaitForExit(5000) -or $incompatible.Process.ExitCode -ne 3) {
    if (-not $incompatible.Process.HasExited) { $incompatible.Process.Kill($true) }
    throw 'The incompatible service did not exit cleanly with code 3.'
}
$incompatible.Process.Dispose()

$reportDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReportPath))
[IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
[ordered]@{
    Timestamp = [DateTimeOffset]::Now; Worker = $worker
    InitialProcessId = $firstProcessId; InitialSessionId = $firstSessionId
    ReplacementProcessId = $second.Ready.processId; ReplacementSessionId = $second.Ready.sessionId
    InitialResponses = $responses; CacheReplay = $cached; InvalidFormula = $invalid
    PostError = $afterError; Recovery = $recovered; ProtocolMismatch = $incompatible.Ready
} | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $ReportPath -Encoding utf8
Write-Output 'Step 13 Math service verification passed: negotiation + one PID reuse + cache + error isolation + clean replacement.'
