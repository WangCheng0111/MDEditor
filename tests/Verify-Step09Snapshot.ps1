param(
    [Parameter(Mandatory)][string]$ReportPath,
    [Parameter(Mandatory)][string]$LayoutsPath
)
$ErrorActionPreference = 'Stop'
$taskReport = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
$taskLayouts = @(Get-Content -LiteralPath $LayoutsPath -Raw | ConvertFrom-Json)
if (-not $taskReport.Passed -or $taskReport.Language -ne 'en-US' -or $taskReport.Patterns -ne 4938 -or
    $taskReport.Exceptions -ne 15 -or $taskReport.Cases.Count -ne 17 -or $taskLayouts.Count -ne 17) {
    throw 'Report/catalog/pinned-language mismatch'
}
foreach ($taskFlag in 'NecessaryBreakVerified','LigatureProtected','ExclusionsProtected','NoFeasibleWidthRejected','SourceUnchanged','SessionLifetimeIndependent') {
    if (-not $taskReport.$taskFlag) { throw "Guard failed: $taskFlag" }
}
$taskLinesCount = 0
$taskNonFinal = 0
$taskHyphens = 0
$taskPixels = 0
$taskMaxWidth = 0.0
$taskMaxSpan = 0.0
foreach ($taskIndex in 0..($taskLayouts.Count - 1)) {
    $taskSnapshot = $taskLayouts[$taskIndex]
    $taskCase = $taskReport.Cases[$taskIndex]
    if (-not $taskCase.Passed -or -not $taskCase.Stable -or -not $taskCase.OnlySpacesOmitted -or $taskCase.Status -ne 'Success') {
        throw 'Case/stability/source guard failed'
    }
    $taskBlock = $taskSnapshot.Blocks[0]
    if ($taskSnapshot.Blocks.Count -ne 1 -or $taskBlock.Lines.Count -ne $taskCase.Lines.Count) { throw 'Block/line count mismatch' }
    $taskCursor = $taskBlock.Source.Start
    foreach ($taskLineIndex in 0..($taskBlock.Lines.Count - 1)) {
        $taskLine = $taskBlock.Lines[$taskLineIndex]
        $taskCheck = $taskCase.Lines[$taskLineIndex]
        $taskNative = $taskCheck.Native
        $taskLinesCount++
        if (-not $taskNative.FinalLine) { $taskNonFinal++ }
        if (-not $taskCheck.Passed -or -not $taskNative.Passed -or -not $taskCheck.SelectedBreakAllowed -or
            -not $taskCheck.SourceCoverage -or -not $taskCheck.GeneratedAnchorValid -or
            -not $taskNative.ProhibitionsValid -or -not $taskNative.GlyphsPreserved -or -not $taskNative.InkSafe) {
            throw 'Line/native/source guard failed'
        }
        if ($taskCheck.Source.Start -ne $taskLine.Source.Start -or $taskCheck.Source.End -ne $taskLine.Source.End -or
            $taskNative.Source.Start -ne $taskLine.Source.Start -or $taskNative.Source.End -ne $taskLine.Source.End -or
            $taskCase.Width -ne $taskLine.Bounds.Width -or $taskNative.TargetWidth -ne $taskCase.Width) {
            throw 'Line metadata mismatch'
        }
        $taskGap = $taskSnapshot.Source.Text.Substring($taskCursor, $taskLine.Source.Start - $taskCursor)
        if ($taskGap.Trim(' ').Length -ne 0) { throw 'Lost non-space source between lines' }
        $taskCursor = $taskLine.Source.End
        $taskText = $taskSnapshot.Source.Text.Substring($taskLine.Source.Start, $taskLine.Source.Length)
        if ($taskText -cne $taskCheck.SourceText) { throw 'Reported source text was replaced by display text' }
        $taskBoundaries = [System.Globalization.StringInfo]::ParseCombiningCharacters($taskSnapshot.Source.Text)
        if ($taskLine.Source.Start -notin $taskBoundaries -or
            ($taskLine.Source.End -ne $taskSnapshot.Source.Length -and $taskLine.Source.End -notin $taskBoundaries)) {
            throw 'Grapheme split at line boundary'
        }
        $taskClusters = @($taskLine.Runs | ForEach-Object { $_.Clusters } | Sort-Object { $_.Source.Start })
        $taskGenerated = @($taskClusters | Where-Object { $null -ne $_.GeneratedText })
        $taskReal = @($taskClusters | Where-Object { $null -eq $_.GeneratedText })
        $taskSourceEnd = $taskLine.Source.Start
        foreach ($taskCluster in $taskReal) {
            if ($taskCluster.Source.Length -le 0 -or $taskCluster.Source.Start -ne $taskSourceEnd) { throw 'Discontinuous source clusters' }
            $taskSourceEnd = $taskCluster.Source.End
        }
        if ($taskSourceEnd -ne $taskLine.Source.End) { throw 'Incomplete source coverage' }
        if ($taskCheck.Suffix.Length -gt 0) {
            $taskHyphens++
            if (-not $taskCheck.Flagged -or $taskNative.FinalLine -or $taskCheck.Suffix -cnotin '-', '‐' -or
                $taskGenerated.Count -ne 1 -or $taskGenerated[0].Source.Length -ne 0 -or
                $taskGenerated[0].Source.Start -ne $taskLine.Source.End -or $taskGenerated[0].GeneratedText -cne $taskCheck.Suffix) {
                throw 'Invalid discretionary/source-end anchor'
            }
        } elseif ($taskCheck.Flagged -or $taskGenerated.Count -ne 0) { throw 'Unselected break generated a hyphen' }
        $taskActual = 0.0
        $taskLeft = [double]::PositiveInfinity
        $taskRight = [double]::NegativeInfinity
        foreach ($taskRun in $taskLine.Runs) {
            if ($taskRun.FontIndex -lt 0 -or $taskRun.FontIndex -ge $taskSnapshot.Fonts.Count) { throw 'Unresolved font slot' }
            $taskWidth = 0.0
            foreach ($taskGlyph in $taskRun.Glyphs) {
                if ($taskGlyph.Index -le 0 -or $taskGlyph.Index -gt 65535 -or $taskGlyph.Advance -lt 0 -or
                    [double][single]$taskGlyph.Advance -ne [double]$taskGlyph.Advance) { throw 'Invalid/missing/non-native glyph' }
                $taskWidth += $taskGlyph.Advance
            }
            if ([math]::Abs($taskWidth - $taskRun.Advance) -gt 1e-7) { throw 'Run sum mismatch' }
            $taskActual += $taskWidth
            # Independently reproduce the specified native translation order, without loading production code.
            $taskAnchor = [double][single]$taskLine.Bounds.X
            $taskX = [double][single]($taskAnchor + ($taskRun.BaselineOrigin.X - $taskLine.Bounds.X))
            if (($taskRun.BidiLevel -band 1) -eq 0) { $taskL = $taskX; $taskR = $taskX + $taskWidth }
            else { $taskL = $taskX - $taskWidth; $taskR = $taskX }
            $taskLeft = [math]::Min($taskLeft, $taskL); $taskRight = [math]::Max($taskRight, $taskR)
            $taskUsed = [bool[]]::new($taskRun.Glyphs.Count)
            foreach ($taskCluster in $taskRun.Clusters) {
                if ($taskCluster.Source.Start -lt $taskRun.Source.Start -or $taskCluster.Source.End -gt $taskRun.Source.End) { throw 'Cluster outside source run' }
                $taskClusterWidth = 0.0
                foreach ($taskG in $taskCluster.GlyphStart..($taskCluster.GlyphStart + $taskCluster.GlyphCount - 1)) {
                    if ($taskG -lt 0 -or $taskG -ge $taskUsed.Length -or $taskUsed[$taskG]) { throw 'Invalid/overlapping glyph cluster' }
                    $taskUsed[$taskG] = $true; $taskClusterWidth += $taskRun.Glyphs[$taskG].Advance
                }
                if ([math]::Abs($taskClusterWidth - $taskCluster.Advance) -gt 1e-7) { throw 'Cluster sum mismatch' }
            }
            if ($taskUsed -contains $false) { throw 'Unmapped glyph' }
        }
        if ($taskNative.Ratio -lt -1 -or $taskNative.Ratio -gt 1) { throw 'Finite spacing bounds exceeded' }
        foreach ($taskSpacing in $taskNative.Spacing) {
            $taskMembers = @($taskReal | Where-Object { $_.Source.Start -eq $taskSpacing.Source.Start -and $_.Source.Length -eq $taskSpacing.Source.Length })
            if ($taskSpacing.Source.Length -le 0 -or $taskMembers.Count -ne 1) { throw 'Spacing applied to synthetic or partial cluster' }
            $taskRun = @($taskLine.Runs | Where-Object { $_.Clusters | Where-Object { $_.Source.Start -eq $taskSpacing.Source.Start -and $_.Source.Length -eq $taskSpacing.Source.Length } })
            if ($taskRun.Count -ne 1) { throw 'Ambiguous spacing segment' }
            $taskEm = $taskRun[0].FontSize
            if ($taskSpacing.Kind -eq 'WordSpace') {
                $taskOriginal = $taskMembers[0].Advance - $taskSpacing.Delta
                if ($taskSpacing.Delta -lt -$taskOriginal / 3 - 1e-5 -or
                    $taskSpacing.Delta -gt [math]::Max(0, 0.5 * $taskEm - $taskOriginal) + 1e-5) { throw 'Word-space capacity exceeded' }
            }
            if ($taskSpacing.Kind -eq 'MixedScript' -and
                ($taskSpacing.Delta -lt 0.125 * $taskEm - 1e-5 -or $taskSpacing.Delta -gt 0.5 * $taskEm + 1e-5)) { throw 'Mixed-script capacity exceeded' }
            if ($taskSpacing.Kind -eq 'InterCharacter' -and
                ($taskSpacing.Delta -lt -1e-5 -or $taskSpacing.Delta -gt 0.24 * $taskEm + 1e-5)) { throw 'CJK capacity exceeded' }
        }
        $taskExpected = $taskNative.TargetWidth
        if ($taskNative.FinalLine -and $taskNative.Ratio -eq 0 -and $taskNative.NaturalWidth -le $taskExpected) { $taskExpected = $taskNative.NaturalWidth }
        $taskWidthError = [math]::Abs($taskActual - $taskExpected)
        $taskSpanError = [math]::Max([math]::Abs($taskLeft - [double][single]$taskLine.Bounds.X), [math]::Abs($taskRight - $taskLeft - $taskExpected))
        $taskMaxWidth = [math]::Max($taskMaxWidth, $taskWidthError); $taskMaxSpan = [math]::Max($taskMaxSpan, $taskSpanError)
        if ($taskWidthError -gt 0.01 -or $taskSpanError -gt 0.01 -or [math]::Abs($taskActual - $taskLine.Advance) -gt 1e-7) { throw 'Real glyph/native-paint alignment failed' }
        if ($taskNative.Pixels.Count -ne 3) { throw 'Missing DPI/scale comparisons' }
        foreach ($taskPixel in $taskNative.Pixels) {
            $taskPixels++
            if ($taskPixel.DifferentBytes -ne 0 -or $taskPixel.MaximumDifference -ne 0) { throw 'Nonzero native pixel difference' }
        }
    }
    if ($taskSnapshot.Source.Text.Substring($taskCursor, $taskBlock.Source.End - $taskCursor).Trim(' ').Length -ne 0) { throw 'Lost non-space source at tail' }
}
if ($taskHyphens -ne $taskReport.HyphenatedLines -or $taskHyphens -eq 0) { throw 'Hyphen evidence mismatch' }
[pscustomobject]@{
    Cases = $taskLayouts.Count; Lines = $taskLinesCount; NonFinalLines = $taskNonFinal
    HyphenatedLines = $taskHyphens; PixelComparisons = $taskPixels
    MaximumActualGlyphError = $taskMaxWidth; MaximumNativePaintSpanError = $taskMaxSpan; Passed = $true
} | ConvertTo-Json
