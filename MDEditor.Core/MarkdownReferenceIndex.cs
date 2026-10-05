using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MDEditor.Core.Text;

namespace MDEditor.Core.Markdown;

public enum MarkdownReplacementKind { FootnoteReference, FootnoteDefinition, EquationReference, Image, ThematicBreak }
public sealed record MarkdownDisplayReplacement(SourceRange Source, string Display, MarkdownReplacementKind Kind);
public sealed record MarkdownImageReference(SourceRange Source, string Alternative, string Target);
public sealed record MarkdownFootnote(string Identifier, int Number, SourceRange Definition,
    ImmutableArray<SourceRange> References);
public sealed record MarkdownEquation(string Identifier, int Number, SourceRange Formula,
    SourceRange LabelSyntax);
public sealed record MarkdownReferenceIssue(SourceRange Source, string Message);

/// <summary>
/// A document-wide, source-versioned dependency index. Reference numbers and targets are
/// recomputed together from the immutable source, so an edited definition cannot leave a
/// stale visual reference behind. All offsets remain UTF-16 positions in the original text.
/// </summary>
public sealed class MarkdownReferenceIndex
{
    private static readonly Regex FootnoteDefinitionPattern = new(
        @"^[ ]{0,3}\[\^(?<id>[^\]\r\n]+)\]:[ \t]*", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex FootnoteReferencePattern = new(
        @"\[\^(?<id>[^\]\r\n]+)\]", RegexOptions.Compiled);
    private static readonly Regex LinkDefinitionPattern = new(
        @"^[ ]{0,3}\[(?<id>[^\]\r\n]+)\]:[ \t]*(?:<(?<angle>[^>\r\n]+)>|(?<target>\S+))",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex EquationLabelPattern = new(
        @"\\label\{(?<id>[A-Za-z0-9:._-]+)\}", RegexOptions.Compiled);
    private static readonly Regex EquationReferencePattern = new(
        @"\\(?<command>eqref|ref)\{(?<id>[A-Za-z0-9:._-]+)\}", RegexOptions.Compiled);
    private static readonly Regex InlineImagePattern = new(
        @"^!\[(?<alt>[^\]]*)\]\((?:<(?<angle>[^>]+)>|(?<target>[^\s)]+))(?:[ \t]+[\""'][^\r\n]*[\""'])?\)$",
        RegexOptions.Compiled);
    private static readonly Regex ReferenceImagePattern = new(
        @"^!\[(?<alt>[^\]]*)\]\[(?<id>[^\]]*)\]$", RegexOptions.Compiled);

    public SourceTextSnapshot Source { get; }
    public ImmutableArray<MarkdownDisplayReplacement> Replacements { get; }
    public ImmutableArray<MarkdownImageReference> Images { get; }
    public ImmutableArray<MarkdownFootnote> Footnotes { get; }
    public ImmutableArray<MarkdownEquation> Equations { get; }
    public ImmutableArray<MarkdownReferenceIssue> Issues { get; }

    private MarkdownReferenceIndex(SourceTextSnapshot source,
        ImmutableArray<MarkdownDisplayReplacement> replacements,
        ImmutableArray<MarkdownImageReference> images,
        ImmutableArray<MarkdownFootnote> footnotes,
        ImmutableArray<MarkdownEquation> equations,
        ImmutableArray<MarkdownReferenceIssue> issues)
    {
        Source = source;
        Replacements = replacements;
        Images = images;
        Footnotes = footnotes;
        Equations = equations;
        Issues = issues;
    }

    public string LayoutContent(MarkdownMathSpan math)
    {
        var raw = Source.GetText(math.Content);
        return math.Kind == MarkdownMathSyntaxKind.Display ?
            EquationLabelPattern.Replace(raw, "") : raw;
    }

    public int SourceOffsetForLayoutError(MarkdownMathSpan math, int layoutOffset)
    {
        var sourceRelative = layoutOffset;
        var removed = 0;
        foreach (Match match in EquationLabelPattern.Matches(Source.GetText(math.Content)))
        {
            if (layoutOffset < match.Index - removed) break;
            sourceRelative += match.Length;
            removed += match.Length;
        }
        return math.Content.Start + Math.Clamp(sourceRelative, 0, math.Content.Length);
    }

    public static MarkdownReferenceIndex Create(SourceTextSnapshot source,
        MarkdownSyntaxDocument syntax, ImmutableArray<MarkdownMathSpan> math)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(syntax);
        if (!ReferenceEquals(source, syntax.Source)) throw new ArgumentException("Foreign syntax.", nameof(syntax));
        var excluded = syntax.Descendants().Where(node => node.Kind is
            MarkdownSyntaxKind.CodeSpan or MarkdownSyntaxKind.FencedCode or MarkdownSyntaxKind.IndentedCode)
            .Select(node => node.Source).ToArray();
        static string Key(string id) => string.Join(' ', id.Trim().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        static SourceRange Range(Match match) => new(match.Index, match.Length);
        bool Excluded(SourceRange range) => excluded.Any(block => block.Start < range.End && range.Start < block.End);
        bool InMath(SourceRange range) => math.Any(span => span.Source.Start < range.End && range.Start < span.Source.End);
        bool Escaped(int position)
        {
            var slashes = 0;
            while (position > slashes && source.Text[position - slashes - 1] == '\\') slashes++;
            return (slashes & 1) != 0;
        }

        var definitions = new Dictionary<string, SourceRange>(StringComparer.Ordinal);
        var definitionPrefixes = new Dictionary<int, SourceRange>();
        foreach (Match match in FootnoteDefinitionPattern.Matches(source.Text))
        {
            var range = Range(match);
            if (Excluded(range)) continue;
            var key = Key(match.Groups["id"].Value);
            if (key.Length == 0) continue;
            if (definitions.TryAdd(key, range)) definitionPrefixes.Add(range.Start, range);
        }
        var linkDefinitions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in LinkDefinitionPattern.Matches(source.Text))
        {
            var range = Range(match);
            if (Excluded(range) || match.Groups["id"].Value.StartsWith('^')) continue;
            var target = match.Groups["angle"].Success ? match.Groups["angle"].Value :
                match.Groups["target"].Value;
            linkDefinitions.TryAdd(Key(match.Groups["id"].Value), target);
        }

        var issues = ImmutableArray.CreateBuilder<MarkdownReferenceIssue>();
        var equations = ImmutableArray.CreateBuilder<MarkdownEquation>();
        var equationByKey = new Dictionary<string, MarkdownEquation>(StringComparer.Ordinal);
        foreach (var span in math.Where(span => span.Kind == MarkdownMathSyntaxKind.Display))
        {
            var text = source.GetText(span.Content);
            var labels = EquationLabelPattern.Matches(text).Cast<Match>().ToArray();
            if (labels.Length == 0) continue;
            var label = labels[0];
            var labelRange = new SourceRange(span.Content.Start + label.Index, label.Length);
            var key = Key(label.Groups["id"].Value);
            if (labels.Length > 1 || equationByKey.ContainsKey(key))
            {
                issues.Add(new(labelRange, "公式标签重复；仅第一个定义生效"));
                continue;
            }
            var equation = new MarkdownEquation(key, equations.Count + 1, span.Source, labelRange);
            equations.Add(equation);
            equationByKey.Add(key, equation);
        }

        var replacements = ImmutableArray.CreateBuilder<MarkdownDisplayReplacement>();
        var footnoteReferences = new Dictionary<string, List<SourceRange>>(StringComparer.Ordinal);
        var footnoteNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in FootnoteReferencePattern.Matches(source.Text))
        {
            var range = Range(match);
            if (definitionPrefixes.ContainsKey(range.Start) || Excluded(range) || InMath(range) ||
                Escaped(range.Start)) continue;
            var key = Key(match.Groups["id"].Value);
            if (!definitions.ContainsKey(key))
            {
                issues.Add(new(range, $"脚注 [{match.Groups["id"].Value}] 没有定义"));
                replacements.Add(new(range, "[?]", MarkdownReplacementKind.FootnoteReference));
                continue;
            }
            if (!footnoteNumbers.TryGetValue(key, out var number))
                footnoteNumbers.Add(key, number = footnoteNumbers.Count + 1);
            if (!footnoteReferences.TryGetValue(key, out var references))
                footnoteReferences.Add(key, references = []);
            references.Add(range);
            replacements.Add(new(range, Superscript(number), MarkdownReplacementKind.FootnoteReference));
        }
        var footnotes = ImmutableArray.CreateBuilder<MarkdownFootnote>();
        foreach (var pair in footnoteNumbers.OrderBy(pair => pair.Value))
        {
            var definition = definitions[pair.Key];
            footnotes.Add(new(pair.Key, pair.Value, definition,
                footnoteReferences[pair.Key].ToImmutableArray()));
            replacements.Add(new(definition, Superscript(pair.Value) + " ",
                MarkdownReplacementKind.FootnoteDefinition));
        }

        foreach (Match match in EquationReferencePattern.Matches(source.Text))
        {
            var range = Range(match);
            if (Excluded(range) || InMath(range) || Escaped(range.Start)) continue;
            var key = Key(match.Groups["id"].Value);
            var found = equationByKey.TryGetValue(key, out var equation);
            if (!found) issues.Add(new(range,
                $"公式引用 {match.Groups["id"].Value} 没有定义"));
            var number = found ? equation!.Number.ToString() : "??";
            var display = match.Groups["command"].Value == "eqref" ? $"({number})" : number;
            replacements.Add(new(range, display, MarkdownReplacementKind.EquationReference));
        }

        var images = ImmutableArray.CreateBuilder<MarkdownImageReference>();
        foreach (var node in syntax.Descendants().Where(node => node.Kind == MarkdownSyntaxKind.Image))
        {
            if (Excluded(node.Source) || InMath(node.Source)) continue;
            var raw = source.GetText(node.Source);
            var inline = InlineImagePattern.Match(raw);
            var reference = inline.Success ? Match.Empty : ReferenceImagePattern.Match(raw);
            var alt = inline.Success ? inline.Groups["alt"].Value :
                reference.Success ? reference.Groups["alt"].Value : "图片";
            var target = inline.Success ? (inline.Groups["angle"].Success ?
                    inline.Groups["angle"].Value : inline.Groups["target"].Value) :
                reference.Success && linkDefinitions.TryGetValue(Key(
                    reference.Groups["id"].Length == 0 ? alt : reference.Groups["id"].Value), out var linked)
                    ? linked : "";
            if (target.Length == 0) issues.Add(new(node.Source, "图片地址缺失或引用未定义"));
            images.Add(new(node.Source, alt, target));
            replacements.Add(new(node.Source, "\uFFFC", MarkdownReplacementKind.Image));
        }

        var ordered = replacements.OrderBy(item => item.Source.Start)
            .ThenByDescending(item => item.Source.Length).ToArray();
        var disjoint = ImmutableArray.CreateBuilder<MarkdownDisplayReplacement>();
        var end = 0;
        foreach (var item in ordered)
            if (item.Source.Start >= end)
            {
                disjoint.Add(item);
                end = item.Source.End;
            }
        return new(source, disjoint.ToImmutable(), images.ToImmutable(), footnotes.ToImmutable(),
            equations.ToImmutable(), issues.ToImmutable());
    }

    private static string Superscript(int number)
    {
        const string digits = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        return string.Concat(number.ToString().Select(digit => digits[digit - '0']));
    }
}
