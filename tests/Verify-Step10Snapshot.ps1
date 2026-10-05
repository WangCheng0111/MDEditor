param(
    [Parameter(Mandatory)][string] $ReportPath,
    [Parameter(Mandatory)][string] $LayoutsPath,
    [string] $LiveReportPath,
    [string] $LiveLayoutPath
)
$ErrorActionPreference = 'Stop'
function Require([bool] $condition, [string] $message) { if (-not $condition) { throw $message } }
function Finite([double] $value) { return -not [double]::IsNaN($value) -and -not [double]::IsInfinity($value) }
function Contains($outer, $inner) { return $inner.Start -ge $outer.Start -and $inner.End -le $outer.End }
$script:Lines = 0
$script:Generated = 0
$script:MaximumAdvanceError = 0.0
$script:MaximumPaintSpanError = 0.0
function Verify($snapshot, [double] $width, $checks) {
    Require (Finite $width) 'Non-finite document width'
    Require ($snapshot.Bounds.Width -eq $width) 'Snapshot/viewport document width differs'
    $text = $snapshot.Source.Text
    $boundaries = [System.Collections.Generic.HashSet[int]]::new()
    [System.Globalization.StringInfo]::ParseCombiningCharacters($text) | ForEach-Object { [void]$boundaries.Add($_) }
    [void]$boundaries.Add($text.Length)
    $lineIndex = 0
    foreach ($block in $snapshot.Blocks) {
        Require (Contains $snapshot.Source.FullRange $block.Source) 'Block source out of bounds'
        $cursor = $block.Source.Start
        foreach ($line in $block.Lines) {
            $script:Lines++
            Require (Contains $block.Source $line.Source) 'Line source out of bounds'
            Require ($boundaries.Contains($line.Source.Start) -and $boundaries.Contains($line.Source.End)) 'Split grapheme boundary'
            Require ($line.Source.Start -ge $cursor) 'Overlapping source lines'
            Require ($text.Substring($cursor, $line.Source.Start - $cursor).Trim(' ').Length -eq 0) 'Omitted non-space source'
            $cursor = $line.Source.End
            $sum = 0.0; $left = [double]::PositiveInfinity; $right = [double]::NegativeInfinity
            $clusters = @()
            foreach ($run in $line.Runs) {
                Require ($run.FontIndex -ge 0 -and $run.FontIndex -lt $snapshot.Fonts.Count) 'Unresolved font slot'
                Require (Contains $line.Source $run.Source) 'Run source out of bounds'
                Require ((Finite $run.FontSize) -and $run.FontSize -gt 0) 'Invalid font size'
                Require ((Finite $run.BaselineOrigin.X) -and (Finite $run.BaselineOrigin.Y)) 'Invalid baseline'
                $used = [bool[]]::new($run.Glyphs.Count); $runSum = 0.0
                foreach ($glyph in $run.Glyphs) {
                    Require ($glyph.Index -gt 0 -and $glyph.Index -le 65535) 'Missing/invalid glyph'
                    Require ((Finite $glyph.Advance) -and $glyph.Advance -ge 0 -and (Finite $glyph.AdvanceOffset) -and (Finite $glyph.AscenderOffset)) 'Invalid glyph metric'
                    $runSum += $glyph.Advance
                }
                foreach ($cluster in $run.Clusters) {
                    Require (Contains $run.Source $cluster.Source) 'Cluster source out of bounds'
                    Require ($cluster.GlyphStart -ge 0 -and $cluster.GlyphCount -gt 0 -and $cluster.GlyphStart + $cluster.GlyphCount -le $run.Glyphs.Count) 'Invalid cluster glyph range'
                    $clusterSum = 0.0
                    for ($g = $cluster.GlyphStart; $g -lt $cluster.GlyphStart + $cluster.GlyphCount; $g++) {
                        Require (-not $used[$g]) 'Overlapping glyph clusters'; $used[$g] = $true
                        $clusterSum += $run.Glyphs[$g].Advance
                    }
                    Require ([Math]::Abs($clusterSum - $cluster.Advance) -lt 0.00001) 'Cluster/glyph advance mismatch'
                    if ($null -ne $cluster.GeneratedText) {
                        Require ($cluster.GeneratedText -in @('-', [string][char]0x2010)) 'Invalid generated text'
                        Require ($cluster.Source.Length -eq 0 -and $cluster.Source.Start -eq $line.Source.End) 'Generated text not anchored to line end'
                        $script:Generated++
                    } else { Require ($cluster.Source.Length -gt 0) 'Unexpected empty source cluster' }
                    $clusters += $cluster
                }
                Require (@($used | Where-Object { -not $_ }).Count -eq 0) 'Uncovered glyph'
                Require ([Math]::Abs($runSum - $run.Advance) -lt 0.00001) 'Run/glyph advance mismatch'
                $sum += $runSum
                # Same staged Single anchor + line-local baseline as production; not metadata Advance alone.
                $x = [double][single]([double][single]$line.Bounds.X + ($run.BaselineOrigin.X - $line.Bounds.X))
                $rtl = ($run.BidiLevel -band 1) -ne 0
                $runLeft = $x; $runRight = $x
                if ($rtl) { $runLeft -= $runSum } else { $runRight += $runSum }
                $left = [Math]::Min($left, $runLeft); $right = [Math]::Max($right, $runRight)
            }
            $sourceCursor = $line.Source.Start; $generatedInLine = 0
            foreach ($cluster in @($clusters | Sort-Object { $_.Source.Start }, { $_.Source.Length })) {
                Require ($cluster.Source.Start -eq $sourceCursor) 'Incomplete/overlapping cluster source'
                $sourceCursor = $cluster.Source.End
                if ($null -ne $cluster.GeneratedText) { $generatedInLine++ }
            }
            Require ($sourceCursor -eq $line.Source.End -and $generatedInLine -le 1) 'Incomplete source or duplicate generated suffix'
            $expected = $line.Advance
            if ($null -ne $checks) {
                $check = $checks[$lineIndex]
                Require ($check.Source.Start -eq $line.Source.Start -and $check.Source.Length -eq $line.Source.Length) 'Report/production source mismatch'
                Require ($check.TargetWidth -eq $width -and $check.Ratio -ge -1 -and $check.Ratio -le 1) 'Invalid target or finite spacing ratio'
                $expected = $width
                if ($check.FinalLine -and $check.Ratio -eq 0) { $expected = $check.NaturalWidth }
                Require ($check.ProhibitionsValid -and $check.GlyphsPreserved -and $check.InkSafe) 'Independent native structural check failed'
                foreach ($pixel in $check.Pixels) { Require ($pixel.DifferentBytes -eq 0 -and $pixel.MaximumDifference -eq 0) 'Nonzero independent native pixel difference' }
            }
            $error = [Math]::Abs($sum - $expected)
            $span = [Math]::Max([Math]::Abs($left - $line.Bounds.X), [Math]::Abs($right - $line.Bounds.X - $expected))
            $script:MaximumAdvanceError = [Math]::Max($script:MaximumAdvanceError, $error)
            $script:MaximumPaintSpanError = [Math]::Max($script:MaximumPaintSpanError, $span)
            Require ($error -lt 0.001 -and $span -lt 0.001) 'Actual glyph advance/paint span failed'
            $lineIndex++
        }
        Require ($text.Substring($cursor, $block.Source.End - $cursor).Trim(' ').Length -eq 0) 'Omitted paragraph tail'
    }
    if ($null -ne $checks) { Require ($lineIndex -eq $checks.Count) 'Report/production line count mismatch' }
}
$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
$layouts = @(Get-Content -LiteralPath $LayoutsPath -Raw | ConvertFrom-Json)
Require ($report.Passed -and $report.WidthChangesBreaks -and $report.RecoveryDeterministic -and $report.NarrowWidthExplicit -and $report.CancellationObserved -and $report.SnapshotSurvivesRelease -and $report.DpiAndHeightIndependent) 'Native reflow report failed'
Require ($report.Cases.Count -eq $layouts.Count -and $report.Cases.Count -ge 4) 'Diagnostic case count mismatch'
$pixels = 0
for ($i = 0; $i -lt $layouts.Count; $i++) {
    $case = $report.Cases[$i]
    Require ($case.CompositeReplay -and $case.Passed) 'Composite replay differs'
    Verify $layouts[$i] $case.Width $case.Lines
    $pixels += @($case.Lines.Pixels).Count
}
if ($LiveReportPath -or $LiveLayoutPath) {
    Require ($LiveReportPath -and $LiveLayoutPath) 'Supply both live paths'
    $live = Get-Content -LiteralPath $LiveReportPath -Raw | ConvertFrom-Json
    $snapshot = Get-Content -LiteralPath $LiveLayoutPath -Raw | ConvertFrom-Json
    Require (-not $live.Pending -and $null -eq $live.Error -and $live.Geometry.Passed) 'Live layout not completed successfully'
    $zoom = [double][single]($live.ZoomPercent / 100.0)
    $expectedWidth = [double][single](($live.Width - 48) / $zoom)
    Require ($live.DocumentWidth -eq $expectedWidth) 'Live width is not derived from view width / editing zoom'
    Require ($live.InfeasibleParagraphs -eq @($live.Sections | Where-Object { -not $_.Feasible }).Count) 'Hidden infeasible paragraphs'
    Require ($snapshot.Blocks.Count + $live.InfeasibleParagraphs -eq $live.Sections.Count) 'Silent missing paragraph'
    Verify $snapshot $expectedWidth $null
    $physicalError = $script:MaximumPaintSpanError * $zoom * $live.Dpi / 96
    Require ($physicalError -lt 0.01) 'Physical right-edge error exceeded 0.01 pixel'
}
[pscustomobject]@{ Passed=$true; Lines=$script:Lines; GeneratedHyphens=$script:Generated; IndependentPixelComparisons=$pixels; MaximumAdvanceError=$script:MaximumAdvanceError; MaximumPaintSpanError=$script:MaximumPaintSpanError } | ConvertTo-Json
