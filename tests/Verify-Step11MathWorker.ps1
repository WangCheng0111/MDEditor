param(
    [string]$WorkerPath = (Join-Path $PSScriptRoot '..\MDEditor.MathWorker\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\MDEditor.MathWorker.exe'),
    [string]$ReportPath = (Join-Path $PSScriptRoot '..\TestResults\step-11-math-worker.json')
)

$ErrorActionPreference = 'Stop'
$worker = [IO.Path]::GetFullPath($WorkerPath)
if (-not [IO.File]::Exists($worker)) { throw "MathWorker is missing: $worker" }

function Invoke-MathWorker([string]$id, [string]$source) {
    $request = [ordered]@{
        protocolVersion = 1; requestId = $id; source = $source
        style = 'display'; emSize = 32.0; fontFamily = 'Cambria Math'
    } | ConvertTo-Json -Compress
    $start = [Diagnostics.ProcessStartInfo]::new($worker)
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($worker)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) { throw 'MathWorker did not start.' }
    $pidValue = $process.Id
    $process.StandardInput.Write($request)
    $process.StandardInput.Close()
    $output = $process.StandardOutput.ReadToEnd()
    $errorText = $process.StandardError.ReadToEnd()
    if (-not $process.WaitForExit(15000)) { $process.Kill($true); throw "MathWorker timed out for $source" }
    $clock.Stop()
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ([string]::IsNullOrWhiteSpace($output)) { throw "MathWorker returned no JSON. stderr: $errorText" }
    [pscustomobject]@{
        ProcessId = $pidValue; ElapsedMilliseconds = $clock.Elapsed.TotalMilliseconds
        ExitCode = $exitCode; Error = $errorText; Response = ($output | ConvertFrom-Json -Depth 30)
    }
}

$cases = @(
    @{ Id = 'fraction'; Source = '\frac{a+b}{c+d}' },
    @{ Id = 'radical'; Source = '\sqrt{x^2+y_1}' },
    @{ Id = 'scripts'; Source = 'x_i^{n+1}' },
    @{ Id = 'nested'; Source = '\frac{x_i^2+1}{\sqrt{y+1}}' }
)
$runs = foreach ($case in $cases) { Invoke-MathWorker $case.Id $case.Source }

foreach ($run in $runs) {
    $response = $run.Response
    if ($run.ExitCode -ne 0 -or -not $response.success -or $null -eq $response.layout) {
        throw "Successful request failed: $($response.errorCode) $($response.errorMessage)"
    }
    $layout = $response.layout
    if ($layout.width -le 0 -or $layout.height -le 0 -or $layout.depth -lt 0 -or
        [Math]::Abs([double]$layout.baseline - [double]$layout.height) -gt 1e-9 -or $layout.glyphs.Count -eq 0) {
        throw "Invalid box geometry for $($layout.source)"
    }
    foreach ($glyph in $layout.glyphs) {
        if ($glyph.glyphIndex -le 0 -or $glyph.fontFamily -ne 'Cambria Math' -or
            $glyph.baselineX -lt 0 -or $glyph.baselineY -lt 0 -or
            $glyph.sourceStart -lt 0 -or $glyph.sourceStart + $glyph.sourceLength -gt $layout.source.Length) {
            throw "Invalid drawable glyph for $($layout.source)"
        }
    }
}
if ($runs[0].Response.layout.rules.Count -ne 1) { throw 'Fraction rule was not extracted.' }
if ($runs[1].Response.layout.rules.Count -ne 1) { throw 'Radical vinculum was not extracted.' }
if (@($runs[2].Response.layout.glyphs | Where-Object fontSize -lt 32).Count -lt 2) { throw 'Script style reduction is absent.' }
if (@($runs | ForEach-Object ProcessId | Sort-Object -Unique).Count -ne $runs.Count) { throw 'Requests did not cross distinct process boundaries.' }

$repeat = Invoke-MathWorker 'nested-repeat' $cases[3].Source
$firstComparable = $runs[3].Response.layout | Select-Object -Property * -ExcludeProperty requestId | ConvertTo-Json -Depth 30 -Compress
$repeatComparable = $repeat.Response.layout | Select-Object -Property * -ExcludeProperty requestId | ConvertTo-Json -Depth 30 -Compress
if ($firstComparable -cne $repeatComparable) { throw 'Repeated worker layout is not deterministic.' }

$invalid = Invoke-MathWorker 'invalid' '\frac{x}'
if ($invalid.ExitCode -ne 2 -or $invalid.Response.success -or $invalid.Response.errorCode -ne 'invalid-formula') {
    throw 'Invalid formula did not return a structured worker error.'
}

$reportDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReportPath))
[IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
$report = [ordered]@{
    Timestamp = [DateTimeOffset]::Now
    Worker = $worker
    SuccessfulRequests = $runs.Count + 1
    DistinctProcesses = @($runs | ForEach-Object ProcessId | Sort-Object -Unique).Count
    Cases = $runs
    Repeat = $repeat
    InvalidFormula = $invalid
}
$report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $ReportPath -Encoding utf8
Write-Output "Step 11 MathWorker verification passed: $($runs.Count + 1) successful requests, $($report.DistinctProcesses) distinct sample processes, structured invalid-formula response."
