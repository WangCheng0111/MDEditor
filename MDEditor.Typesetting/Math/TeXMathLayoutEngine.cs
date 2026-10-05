using System.Text;

namespace MDEditor.Typesetting.Mathematics;

public sealed class MathParseException : Exception
{
    public int Position { get; }
    public MathParseException(string message, int position) : base($"{message} (position {position})") => Position = position;
}

/// <summary>
/// Deterministic TeX box-model kernel. It consumes OpenType MATH constants and vertical
/// variants when the platform adapter provides them, while remaining portable and testable.
/// </summary>
public sealed class TeXMathLayoutEngine
{
    private readonly IMathGlyphMetricsProvider _metrics;
    public TeXMathLayoutEngine(IMathGlyphMetricsProvider metrics) => _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

    public MathLayoutResult Layout(MathLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request); request.Validate();
        var root = new Parser(request.Source).Parse();
        var context = new Context(request.EmSize, request.FontFamily, _metrics as IOpenTypeMathMetricsProvider);
        var box = LayoutNode(root, request.Style, context);
        if (box.Glyphs.Count == 0) throw new MathParseException("Formula contains no drawable symbols", 0);
        return new MathLayoutResult(request.RequestId, request.Source, box.Width, box.Height, box.Depth,
            box.Glyphs.Select(g => new MathGlyphPlacement
            {
                Text = g.Metric.Text, FontFamily = g.Metric.FontFamily, FontFace = g.Metric.FontFace,
                GlyphIndex = g.Metric.GlyphIndex, FontSize = g.FontSize,
                BaselineX = g.X, BaselineY = g.BaselineY, Advance = g.Metric.Advance,
                AdvanceOffset = g.Metric.AdvanceOffset, AscenderOffset = g.Metric.AscenderOffset, Role = g.Role,
                SourceStart = g.SourceStart, SourceLength = g.SourceLength
            }), box.Rules.Select(r => new MathRulePlacement
            {
                X = r.X, Y = r.Y, Width = r.Width, Height = r.Height,
                SourceStart = r.SourceStart, SourceLength = r.SourceLength
            }));
    }

    private Box LayoutNode(Node node, MathLayoutStyle style, Context context) => node switch
    {
        SequenceNode sequence => LayoutSequence(sequence, style, context),
        SymbolNode symbol => LayoutSymbol(symbol, style, context),
        FractionNode fraction => LayoutFraction(fraction, style, context),
        RadicalNode radical => LayoutRadical(radical, style, context),
        ScriptsNode scripts => LayoutScripts(scripts, style, context),
        _ => throw new InvalidOperationException($"Unknown math node {node.GetType().Name}.")
    };

    private Box LayoutSequence(SequenceNode sequence, MathLayoutStyle style, Context context)
    {
        var children = sequence.Children.Select(child => LayoutNode(child, style, context)).ToArray();
        if (children.Length == 0) return Box.Empty;
        var height = children.Max(child => child.Height);
        var depth = children.Max(child => child.Depth);
        var result = new Box(children.Sum(child => child.Width), height, depth);
        double x = 0;
        foreach (var child in children)
        {
            result.Add(child, x, height - child.Height);
            x += child.Width;
        }
        return result;
    }

    private Box LayoutSymbol(SymbolNode symbol, MathLayoutStyle style, Context context)
    {
        var size = context.Size(style);
        var text = MathAlphabet(symbol.Text);
        if (style == MathLayoutStyle.Display && text is "∑" or "∏" or "∫" && context.Advanced is not null)
        {
            var constants = context.Constants(style);
            var target = Math.Max(size, constants.DisplayOperatorMinHeight);
            var vertical = context.Advanced.StretchVertical(text, context.FontFamily, size, target);
            var baseline = Math.Clamp(vertical.Height / 2 + constants.AxisHeight, size * 0.05, vertical.Height);
            var result = new Box(vertical.Width, baseline, vertical.Height - baseline);
            result.Add(vertical, 0, 0, symbol.Start, symbol.Length);
            return result;
        }
        var metric = _metrics.Measure(text, context.FontFamily, size);
        ValidateMetric(metric, text, size);
        var width = Math.Max(metric.Advance, size * 0.02);
        var height = Math.Max(metric.Ascent, size * 0.05);
        var box = new Box(width, height, Math.Max(0, metric.Descent));
        box.Glyphs.Add(new GlyphDraft(metric, size, 0, height, symbol.Start, symbol.Length, MathGlyphRole.Regular));
        return box;
    }

    private Box LayoutFraction(FractionNode fraction, MathLayoutStyle style, Context context)
    {
        var childStyle = NextScriptStyle(style);
        var numerator = LayoutNode(fraction.Numerator, childStyle, context);
        var denominator = LayoutNode(fraction.Denominator, childStyle, context);
        var em = context.Size(style); var constants = context.Constants(style);
        var rule = Math.Max(1.0 / 96, constants.FractionRuleThickness);
        var numeratorShift = style == MathLayoutStyle.Display ? constants.FractionNumeratorDisplayStyleShiftUp : constants.FractionNumeratorShiftUp;
        var denominatorShift = style == MathLayoutStyle.Display ? constants.FractionDenominatorDisplayStyleShiftDown : constants.FractionDenominatorShiftDown;
        var numeratorGap = style == MathLayoutStyle.Display ? constants.FractionNumeratorDisplayStyleGapMin : constants.FractionNumeratorGapMin;
        var denominatorGap = style == MathLayoutStyle.Display ? constants.FractionDenominatorDisplayStyleGapMin : constants.FractionDenominatorGapMin;
        var pad = em * 0.12;
        var width = Math.Max(numerator.Width, denominator.Width) + 2 * pad;
        var barCenter = -constants.AxisHeight; var barTop = barCenter - rule / 2; var barBottom = barCenter + rule / 2;
        var numeratorBaseline = Math.Min(-numeratorShift, barTop - numeratorGap - numerator.Depth);
        var denominatorBaseline = Math.Max(denominatorShift, barBottom + denominatorGap + denominator.Height);
        var top = Math.Min(numeratorBaseline - numerator.Height, barTop);
        var bottom = Math.Max(denominatorBaseline + denominator.Depth, barBottom);
        var shift = -top; var baseline = shift;
        var result = new Box(width, baseline, bottom - top - baseline);
        result.Add(numerator, (width - numerator.Width) / 2, numeratorBaseline - numerator.Height + shift);
        result.Rules.Add(new RuleDraft(0, barTop + shift, width, rule, fraction.Start, fraction.Length));
        result.Add(denominator, (width - denominator.Width) / 2, denominatorBaseline - denominator.Height + shift);
        return result;
    }

    private Box LayoutRadical(RadicalNode radical, MathLayoutStyle style, Context context)
    {
        var radicand = LayoutNode(radical.Radicand, style, context);
        var em = context.Size(style); var constants = context.Constants(style);
        var rule = Math.Max(1.0 / 96, constants.RadicalRuleThickness);
        var gap = style == MathLayoutStyle.Display ? constants.RadicalDisplayStyleVerticalGap : constants.RadicalVerticalGap;
        var extra = constants.RadicalExtraAscender;
        var target = radicand.Height + radicand.Depth + gap + rule;
        var root = StretchVertical("√", em, target, context);
        // The MATH assembly's top part has an internal top bearing. Grow the assembly until
        // the visible extent below that connector covers the radicand, then move the advance
        // box upward so the connector ink (not its box edge) meets the continuation rule.
        for (var attempt = 0; attempt < 3 && root.Height - root.TopConnectorY < target - 1e-7; attempt++)
            root = StretchVertical("√", em, target + root.TopConnectorY, context);
        var ruleY = extra;
        var rootY = ruleY - root.TopConnectorY;
        var childX = root.Width + em * 0.02;
        var childY = ruleY + rule + gap;
        var baseline = childY + radicand.Height;
        var total = Math.Max(rootY + root.Height, childY + radicand.Height + radicand.Depth);
        if (baseline > total) total = baseline;
        var width = childX + radicand.Width + em * 0.04;
        var result = new Box(width, baseline, Math.Max(0, total - baseline));
        result.Add(root, 0, rootY, radical.Start, Math.Min(radical.Length, 5));
        result.Add(radicand, childX, childY);
        // Overlap by half a rule thickness: enough to seal antialiasing at every DPI, without
        // painting a second bar over the short connector already present in the top glyph.
        var ruleX = Math.Clamp(root.TopConnectorEndX - rule * 0.5, 0, childX);
        result.Rules.Add(new RuleDraft(ruleX, ruleY, width - ruleX, rule, radical.Start, radical.Length));
        return result;
    }

    private Box LayoutScripts(ScriptsNode scripts, MathLayoutStyle style, Context context)
    {
        var basis = LayoutNode(scripts.Base, style, context);
        var childStyle = NextScriptStyle(style);
        var superscript = scripts.Superscript is null ? null : LayoutNode(scripts.Superscript, childStyle, context);
        var subscript = scripts.Subscript is null ? null : LayoutNode(scripts.Subscript, childStyle, context);
        var em = context.Size(style); var constants = context.Constants(style);
        var scriptX = basis.Width + em * 0.04;
        var baseline = basis.Height;
        double minTop = 0, maxBottom = basis.Height + basis.Depth;
        double superBaseline = 0, subBaseline = 0;
        if (superscript is not null)
        {
            superBaseline = baseline - Math.Max(constants.SuperscriptShiftUp, superscript.Depth + constants.SuperscriptBottomMin);
            minTop = Math.Min(minTop, superBaseline - superscript.Height);
        }
        if (subscript is not null)
        {
            subBaseline = baseline + Math.Max(constants.SubscriptShiftDown, subscript.Height - constants.SubscriptTopMax);
            maxBottom = Math.Max(maxBottom, subBaseline + subscript.Depth);
        }
        if (superscript is not null && subscript is not null)
        {
            var separation = (subBaseline - subscript.Height) - (superBaseline + superscript.Depth);
            var minimum = constants.SubSuperscriptGapMin;
            if (separation < minimum) subBaseline += minimum - separation;
            maxBottom = Math.Max(maxBottom, subBaseline + subscript.Depth);
        }
        var shift = -minTop;
        var scriptWidth = Math.Max(superscript?.Width ?? 0, subscript?.Width ?? 0);
        var result = new Box(scriptX + scriptWidth + constants.SpaceAfterScript, baseline + shift, maxBottom - baseline);
        result.Add(basis, 0, shift);
        if (superscript is not null) result.Add(superscript, scriptX, superBaseline - superscript.Height + shift);
        if (subscript is not null) result.Add(subscript, scriptX, subBaseline - subscript.Height + shift);
        return result;
    }

    private MathVerticalGlyph StretchVertical(string text, double em, double target, Context context)
    {
        if (context.Advanced is not null) return context.Advanced.StretchVertical(text, context.FontFamily, em, target);
        var size = em; var metric = _metrics.Measure(text, context.FontFamily, size); ValidateMetric(metric, text, size);
        var actual = metric.Ascent + metric.Descent;
        if (actual < target && actual > 0)
        {
            size = Math.Min(em * 8, em * target / actual);
            metric = _metrics.Measure(text, context.FontFamily, size); ValidateMetric(metric, text, size);
        }
        return new(Math.Max(metric.Advance, size * 0.02), metric.Ascent + metric.Descent,
            [new MathVerticalGlyphPart { Metric = metric, FontSize = size, BaselineY = metric.Ascent, Role = MathGlyphRole.VerticalVariant }]);
    }

    private static void ValidateMetric(MathGlyphMetric metric, string text, double size)
    {
        ArgumentNullException.ThrowIfNull(metric);
        if (metric.Text != text || string.IsNullOrWhiteSpace(metric.FontFamily) || string.IsNullOrWhiteSpace(metric.FontFace) ||
            metric.GlyphIndex is < 0 or > ushort.MaxValue || !double.IsFinite(metric.Advance) || metric.Advance < 0 ||
            !double.IsFinite(metric.Ascent) || metric.Ascent <= 0 || !double.IsFinite(metric.Descent) || metric.Descent < 0 ||
            !double.IsFinite(metric.AdvanceOffset) || !double.IsFinite(metric.AscenderOffset))
            throw new InvalidOperationException($"Invalid glyph metric for '{text}' at {size} DIPs.");
    }

    private static MathLayoutStyle NextScriptStyle(MathLayoutStyle style) => style switch
    {
        MathLayoutStyle.Display or MathLayoutStyle.Text => MathLayoutStyle.Script,
        _ => MathLayoutStyle.ScriptScript
    };

    private static string MathAlphabet(string text)
    {
        if (text.Length != 1) return text;
        var c = text[0];
        if (c is >= 'A' and <= 'Z') return char.ConvertFromUtf32(0x1D434 + c - 'A');
        if (c is >= 'a' and <= 'z') return c == 'h' ? "ℎ" : char.ConvertFromUtf32(0x1D44E + c - 'a');
        return text;
    }

    private sealed class Context
    {
        public double EmSize { get; }
        public string FontFamily { get; }
        public IOpenTypeMathMetricsProvider? Advanced { get; }
        private readonly MathFontConstants _base;
        public Context(double emSize, string fontFamily, IOpenTypeMathMetricsProvider? advanced)
        {
            EmSize = emSize; FontFamily = fontFamily; Advanced = advanced;
            _base = advanced?.GetMathConstants(fontFamily, emSize) ?? Fallback(emSize);
            ValidateConstants(_base);
        }
        public double Size(MathLayoutStyle style) => EmSize * (style switch
        {
            MathLayoutStyle.Display or MathLayoutStyle.Text => 1,
            MathLayoutStyle.Script => _base.ScriptScale,
            MathLayoutStyle.ScriptScript => _base.ScriptScriptScale,
            _ => throw new ArgumentOutOfRangeException(nameof(style))
        });
        public MathFontConstants Constants(MathLayoutStyle style)
        {
            var size = Size(style);
            var result = Advanced?.GetMathConstants(FontFamily, size) ?? Fallback(size);
            ValidateConstants(result); return result;
        }
        private static MathFontConstants Fallback(double em) => new()
        {
            ScriptScale = .70, ScriptScriptScale = .50, DisplayOperatorMinHeight = em * 1.35,
            AxisHeight = em * .25, SubscriptShiftDown = em * .30, SubscriptTopMax = em * .40,
            SuperscriptShiftUp = em * .45, SuperscriptBottomMin = em * .18,
            SubSuperscriptGapMin = em * .20, SpaceAfterScript = em / 24,
            FractionNumeratorShiftUp = em * .55, FractionNumeratorDisplayStyleShiftUp = em * .70,
            FractionDenominatorShiftDown = em * .55, FractionDenominatorDisplayStyleShiftDown = em * .70,
            FractionNumeratorGapMin = em * .14, FractionNumeratorDisplayStyleGapMin = em * .20,
            FractionRuleThickness = em * .045, FractionDenominatorGapMin = em * .14,
            FractionDenominatorDisplayStyleGapMin = em * .20, RadicalVerticalGap = em * .10,
            RadicalDisplayStyleVerticalGap = em * .14, RadicalRuleThickness = em * .045,
            RadicalExtraAscender = em * .045
        };
        private static void ValidateConstants(MathFontConstants value)
        {
            var numbers = new[] { value.ScriptScale, value.ScriptScriptScale, value.DisplayOperatorMinHeight,
                value.AxisHeight, value.SubscriptShiftDown, value.SubscriptTopMax, value.SuperscriptShiftUp,
                value.SuperscriptBottomMin, value.SubSuperscriptGapMin, value.SpaceAfterScript,
                value.FractionNumeratorShiftUp, value.FractionNumeratorDisplayStyleShiftUp,
                value.FractionDenominatorShiftDown, value.FractionDenominatorDisplayStyleShiftDown,
                value.FractionNumeratorGapMin, value.FractionNumeratorDisplayStyleGapMin,
                value.FractionRuleThickness, value.FractionDenominatorGapMin,
                value.FractionDenominatorDisplayStyleGapMin, value.RadicalVerticalGap,
                value.RadicalDisplayStyleVerticalGap, value.RadicalRuleThickness, value.RadicalExtraAscender };
            if (numbers.Any(number => !double.IsFinite(number) || number < 0) || value.ScriptScale is <= 0 or > 1 ||
                value.ScriptScriptScale is <= 0 or > 1 || value.FractionRuleThickness <= 0 || value.RadicalRuleThickness <= 0)
                throw new InvalidOperationException("The font returned invalid OpenType MATH constants.");
        }
    }

    private sealed class Box(double width, double height, double depth)
    {
        public static Box Empty => new(0, 0, 0);
        public double Width { get; } = width;
        public double Height { get; } = height;
        public double Depth { get; } = depth;
        public List<GlyphDraft> Glyphs { get; } = [];
        public List<RuleDraft> Rules { get; } = [];
        public void Add(Box child, double x, double y)
        {
            Glyphs.AddRange(child.Glyphs.Select(g => g with { X = g.X + x, BaselineY = g.BaselineY + y }));
            Rules.AddRange(child.Rules.Select(r => r with { X = r.X + x, Y = r.Y + y }));
        }
        public void Add(MathVerticalGlyph vertical, double x, double y, int sourceStart, int sourceLength)
        {
            foreach (var part in vertical.Parts)
                Glyphs.Add(new(part.Metric, part.FontSize, x + part.BaselineX, y + part.BaselineY,
                    sourceStart, sourceLength, part.Role));
        }
    }

    private sealed record GlyphDraft(MathGlyphMetric Metric, double FontSize, double X, double BaselineY,
        int SourceStart, int SourceLength, MathGlyphRole Role);
    private sealed record RuleDraft(double X, double Y, double Width, double Height, int SourceStart, int SourceLength);

    private abstract record Node(int Start, int Length);
    private sealed record SequenceNode(int Start, int Length, IReadOnlyList<Node> Children) : Node(Start, Length);
    private sealed record SymbolNode(int Start, int Length, string Text) : Node(Start, Length);
    private sealed record FractionNode(int Start, int Length, Node Numerator, Node Denominator) : Node(Start, Length);
    private sealed record RadicalNode(int Start, int Length, Node Radicand) : Node(Start, Length);
    private sealed record ScriptsNode(int Start, int Length, Node Base, Node? Subscript, Node? Superscript) : Node(Start, Length);

    private sealed class Parser(string source)
    {
        private static readonly IReadOnlyDictionary<string, string> Commands = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["theta"] = "θ",
            ["lambda"] = "λ", ["mu"] = "μ", ["pi"] = "π", ["sigma"] = "σ", ["phi"] = "φ", ["omega"] = "ω",
            ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["infty"] = "∞", ["cdot"] = "·", ["times"] = "×",
            ["pm"] = "±", ["le"] = "≤", ["ge"] = "≥", ["neq"] = "≠", ["to"] = "→"
        };
        private int _position;

        public Node Parse()
        {
            var result = ParseSequence(false);
            SkipWhitespace();
            if (_position != source.Length) throw Error("Unexpected input");
            return result;
        }

        private Node ParseSequence(bool grouped)
        {
            var start = _position;
            var children = new List<Node>();
            while (_position < source.Length)
            {
                SkipWhitespace();
                if (_position >= source.Length) break;
                if (source[_position] == '}')
                {
                    if (!grouped) throw Error("Unexpected closing brace");
                    break;
                }
                if (source[_position] is '_' or '^') throw Error("Script marker has no base");
                var atom = ParseAtom();
                Node? sub = null, sup = null;
                while (_position < source.Length && source[_position] is '_' or '^')
                {
                    var marker = source[_position++];
                    var argument = ParseRequiredArgument(marker == '_' ? "subscript" : "superscript");
                    if (marker == '_') { if (sub is not null) throw Error("Duplicate subscript"); sub = argument; }
                    else { if (sup is not null) throw Error("Duplicate superscript"); sup = argument; }
                }
                if (sub is not null || sup is not null)
                    atom = new ScriptsNode(atom.Start, _position - atom.Start, atom, sub, sup);
                children.Add(atom);
            }
            return new SequenceNode(start, _position - start, children);
        }

        private Node ParseAtom()
        {
            var start = _position;
            if (source[_position] == '{') return ParseGroup();
            if (source[_position] == '\\')
            {
                _position++;
                if (_position >= source.Length) throw Error("Trailing escape");
                if (!char.IsLetter(source[_position]))
                {
                    var literal = source[_position++].ToString();
                    return new SymbolNode(start, _position - start, literal);
                }
                var nameStart = _position;
                while (_position < source.Length && char.IsLetter(source[_position])) _position++;
                var name = source[nameStart.._position];
                if (name == "frac")
                {
                    var numerator = ParseRequiredGroup("fraction numerator");
                    var denominator = ParseRequiredGroup("fraction denominator");
                    return new FractionNode(start, _position - start, numerator, denominator);
                }
                if (name == "sqrt")
                {
                    var radicand = ParseRequiredGroup("radicand");
                    return new RadicalNode(start, _position - start, radicand);
                }
                if (!Commands.TryGetValue(name, out var symbol)) throw new MathParseException($"Unsupported command \\{name}", start);
                return new SymbolNode(start, _position - start, symbol);
            }
            var rune = Rune.GetRuneAt(source, _position);
            _position += rune.Utf16SequenceLength;
            return new SymbolNode(start, rune.Utf16SequenceLength, rune.ToString());
        }

        private Node ParseRequiredArgument(string name)
        {
            SkipWhitespace();
            if (_position >= source.Length) throw Error($"Missing {name}");
            if (source[_position] is '_' or '^') throw Error($"Missing {name}");
            return source[_position] == '{' ? ParseGroup() : ParseAtom();
        }
        private Node ParseRequiredGroup(string name)
        {
            SkipWhitespace();
            if (_position >= source.Length || source[_position] != '{') throw Error($"Missing {name} group");
            return ParseGroup();
        }
        private Node ParseGroup()
        {
            var start = _position++;
            var content = ParseSequence(true);
            if (_position >= source.Length || source[_position] != '}') throw Error("Unclosed group");
            _position++;
            return content with { Start = start, Length = _position - start };
        }
        private void SkipWhitespace() { while (_position < source.Length && char.IsWhiteSpace(source[_position])) _position++; }
        private MathParseException Error(string message) => new(message, _position);
    }
}
