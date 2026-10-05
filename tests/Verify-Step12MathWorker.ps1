param(
    [string]$WorkerPath = (Join-Path $PSScriptRoot '..\MDEditor.MathWorker\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\MDEditor.MathWorker.exe'),
    [string]$ReportPath = (Join-Path $PSScriptRoot '..\TestResults\step-12-math-worker.json')
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
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) { throw 'MathWorker did not start.' }
    $pidValue = $process.Id
    $process.StandardInput.Write($request); $process.StandardInput.Close()
    $output = $process.StandardOutput.ReadToEnd(); $errorText = $process.StandardError.ReadToEnd()
    if (-not $process.WaitForExit(20000)) { $process.Kill($true); throw "MathWorker timed out for $id" }
    $clock.Stop(); $exitCode = $process.ExitCode; $process.Dispose()
    if ([string]::IsNullOrWhiteSpace($output)) { throw "MathWorker returned no JSON. stderr: $errorText" }
    [pscustomobject]@{
        ProcessId = $pidValue; ElapsedMilliseconds = $clock.Elapsed.TotalMilliseconds
        ExitCode = $exitCode; Error = $errorText; Response = ($output | ConvertFrom-Json -Depth 100)
    }
}

$inner = '1'
for ($index = 0; $index -lt 12; $index++) { $inner = "\frac{1}{$inner}" }
$cases = @(
    @{ Id = 'nested'; Source = '\frac{x_i^2+1}{\sqrt{y+1}}' },
    @{ Id = 'assembly'; Source = "\sqrt{$inner}" },
    @{ Id = 'operator'; Source = '\sum_{i=1}^{n}\frac{1}{i^2}' }
)
$runs = @($cases | ForEach-Object { Invoke-MathWorker $_.Id $_.Source })
foreach ($run in $runs) {
    $layout = $run.Response.layout
    if ($run.ExitCode -ne 0 -or -not $run.Response.success -or $null -eq $layout) {
        throw "Successful request failed: $($run.Response.errorCode) $($run.Response.errorMessage)"
    }
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
$allGlyphs = @($runs | ForEach-Object { $_.Response.layout.glyphs })
if (@($allGlyphs | Where-Object role -eq 'verticalVariant').Count -eq 0) { throw 'Vertical variant is absent.' }
if (@($allGlyphs | Where-Object role -eq 'verticalAssemblyPart').Count -eq 0) { throw 'Vertical assembly is absent.' }
if (($runs | ForEach-Object { $_.Response.layout.rules.Count } | Measure-Object -Sum).Sum -lt 3) { throw 'Math rules are absent.' }
if (@($runs | ForEach-Object ProcessId | Sort-Object -Unique).Count -ne $runs.Count) { throw 'Requests did not use distinct processes.' }

$repeat = Invoke-MathWorker 'assembly-repeat' $cases[1].Source
$firstComparable = $runs[1].Response.layout | Select-Object -Property * -ExcludeProperty requestId | ConvertTo-Json -Depth 100 -Compress
$repeatComparable = $repeat.Response.layout | Select-Object -Property * -ExcludeProperty requestId | ConvertTo-Json -Depth 100 -Compress
if ($firstComparable -cne $repeatComparable) { throw 'Repeated assembly layout is not deterministic.' }
$invalid = Invoke-MathWorker 'invalid' '\sqrt{x'
if ($invalid.ExitCode -ne 2 -or $invalid.Response.success -or $invalid.Response.errorCode -ne 'invalid-formula') {
    throw 'Invalid formula did not return a structured worker error.'
}

$reportDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReportPath))
[IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
[ordered]@{
    Timestamp = [DateTimeOffset]::Now; Worker = $worker
    SuccessfulRequests = $runs.Count + 1
    DistinctProcesses = @($runs | ForEach-Object ProcessId | Sort-Object -Unique).Count
    VerticalVariants = @($allGlyphs | Where-Object role -eq 'verticalVariant').Count
    AssemblyParts = @($allGlyphs | Where-Object role -eq 'verticalAssemblyPart').Count
    Cases = $runs; Repeat = $repeat; InvalidFormula = $invalid
} | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $ReportPath -Encoding utf8
Write-Output "Step 12 MathWorker verification passed: MATH variant + assembly + rules + deterministic replay + structured error."
