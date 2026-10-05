using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MDEditor.Typesetting.Mathematics;

public enum MathLayoutStyle { Display, Text, Script, ScriptScript }
public enum MathGlyphRole { Regular, VerticalVariant, VerticalAssemblyPart }

/// <summary>First line of a persistent MathWorker session. The ranges allow future negotiation.</summary>
public sealed record MathWorkerHandshakeRequest
{
    public const int CurrentTransportVersion = 1;
    public string MessageType { get; init; } = "hello";
    public int MinimumProtocolVersion { get; init; } = MathLayoutRequest.CurrentProtocolVersion;
    public int MaximumProtocolVersion { get; init; } = MathLayoutRequest.CurrentProtocolVersion;
    public int TransportVersion { get; init; } = CurrentTransportVersion;
    public string ClientId { get; init; } = "";

    public void Validate()
    {
        if (MessageType != "hello") throw new ArgumentException("The handshake message type must be 'hello'.");
        if (TransportVersion != CurrentTransportVersion)
            throw new ArgumentException($"Unsupported transport version {TransportVersion}.");
        if (MinimumProtocolVersion <= 0 || MaximumProtocolVersion < MinimumProtocolVersion)
            throw new ArgumentException("The protocol version range is invalid.");
        if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 128)
            throw new ArgumentException("ClientId must contain 1-128 characters.");
    }
}

/// <summary>Negotiated session metadata returned before any layout request is accepted.</summary>
public sealed record MathWorkerHandshakeResponse
{
    public string MessageType { get; init; } = "ready";
    public bool Success { get; init; }
    public int SelectedProtocolVersion { get; init; }
    public int TransportVersion { get; init; } = MathWorkerHandshakeRequest.CurrentTransportVersion;
    public int ProcessId { get; init; }
    public string SessionId { get; init; } = "";
    public int CacheCapacity { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>One formula request. All geometry is returned in document DIPs.</summary>
public sealed record MathLayoutRequest
{
    public const int CurrentProtocolVersion = 1;
    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;
    public string RequestId { get; init; } = "";
    public string Source { get; init; } = "";
    public MathLayoutStyle Style { get; init; } = MathLayoutStyle.Display;
    public double EmSize { get; init; } = 32;
    public string FontFamily { get; init; } = "Cambria Math";

    public void Validate()
    {
        if (ProtocolVersion != CurrentProtocolVersion)
            throw new ArgumentException($"Unsupported math protocol version {ProtocolVersion}.", nameof(ProtocolVersion));
        if (string.IsNullOrWhiteSpace(RequestId) || RequestId.Length > 128)
            throw new ArgumentException("RequestId must contain 1-128 characters.", nameof(RequestId));
        if (string.IsNullOrWhiteSpace(Source) || Source.Length > 4096)
            throw new ArgumentException("Formula source must contain 1-4096 characters.", nameof(Source));
        if (!double.IsFinite(EmSize) || EmSize < 6 || EmSize > 512)
            throw new ArgumentOutOfRangeException(nameof(EmSize), "EmSize must be 6-512 DIPs.");
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 128)
            throw new ArgumentException("FontFamily must contain 1-128 characters.", nameof(FontFamily));
        if (!Enum.IsDefined(Style)) throw new ArgumentOutOfRangeException(nameof(Style));
    }
}

/// <summary>Resolved glyph metric supplied by the platform/font adapter.</summary>
public sealed record MathGlyphMetric
{
    public required string Text { get; init; }
    public required string FontFamily { get; init; }
    public required string FontFace { get; init; }
    public int GlyphIndex { get; init; }
    public double Advance { get; init; }
    public double Ascent { get; init; }
    public double Descent { get; init; }
    public double AdvanceOffset { get; init; }
    public double AscenderOffset { get; init; }
}

public interface IMathGlyphMetricsProvider
{
    MathGlyphMetric Measure(string text, string fontFamily, double emSize);
}

/// <summary>Font-derived values used by the recursive TeX box model, already scaled to DIPs.</summary>
public sealed record MathFontConstants
{
    public double ScriptScale { get; init; }
    public double ScriptScriptScale { get; init; }
    public double DisplayOperatorMinHeight { get; init; }
    public double AxisHeight { get; init; }
    public double SubscriptShiftDown { get; init; }
    public double SubscriptTopMax { get; init; }
    public double SuperscriptShiftUp { get; init; }
    public double SuperscriptBottomMin { get; init; }
    public double SubSuperscriptGapMin { get; init; }
    public double SpaceAfterScript { get; init; }
    public double FractionNumeratorShiftUp { get; init; }
    public double FractionNumeratorDisplayStyleShiftUp { get; init; }
    public double FractionDenominatorShiftDown { get; init; }
    public double FractionDenominatorDisplayStyleShiftDown { get; init; }
    public double FractionNumeratorGapMin { get; init; }
    public double FractionNumeratorDisplayStyleGapMin { get; init; }
    public double FractionRuleThickness { get; init; }
    public double FractionDenominatorGapMin { get; init; }
    public double FractionDenominatorDisplayStyleGapMin { get; init; }
    public double RadicalVerticalGap { get; init; }
    public double RadicalDisplayStyleVerticalGap { get; init; }
    public double RadicalRuleThickness { get; init; }
    public double RadicalExtraAscender { get; init; }
}

/// <summary>One glyph in a ready-made vertical variant or an OpenType MATH assembly.</summary>
public sealed record MathVerticalGlyphPart
{
    public required MathGlyphMetric Metric { get; init; }
    public double FontSize { get; init; }
    public double BaselineX { get; init; }
    public double BaselineY { get; init; }
    public MathGlyphRole Role { get; init; }
}

/// <summary>A font-selected vertical glyph variant or an assembly, in top-left coordinates.</summary>
public sealed class MathVerticalGlyph
{
    public double Width { get; }
    public double Height { get; }
    /// <summary>Top edge of the font's continuation connector, relative to this glyph box.</summary>
    public double TopConnectorY { get; }
    /// <summary>Right edge of the font's continuation connector, relative to this glyph box.</summary>
    public double TopConnectorEndX { get; }
    public ImmutableArray<MathVerticalGlyphPart> Parts { get; }

    public MathVerticalGlyph(double width, double height, IEnumerable<MathVerticalGlyphPart> parts,
        double topConnectorY = 0, double? topConnectorEndX = null)
    {
        PositiveFinite(width, nameof(width)); PositiveFinite(height, nameof(height));
        Finite(topConnectorY, nameof(topConnectorY));
        var connectorEnd = topConnectorEndX ?? width;
        Finite(connectorEnd, nameof(topConnectorEndX));
        if (topConnectorY < 0 || topConnectorY > height + 1e-7)
            throw new ArgumentOutOfRangeException(nameof(topConnectorY), "Must be inside the vertical glyph box.");
        if (connectorEnd <= 0 || connectorEnd > width + 1e-7)
            throw new ArgumentOutOfRangeException(nameof(topConnectorEndX), "Must be inside the vertical glyph box.");
        Width = width; Height = height; TopConnectorY = topConnectorY; TopConnectorEndX = connectorEnd;
        ArgumentNullException.ThrowIfNull(parts); Parts = parts.ToImmutableArray();
        if (Parts.IsEmpty) throw new ArgumentException("A vertical glyph must contain at least one part.", nameof(parts));
        foreach (var part in Parts)
        {
            ArgumentNullException.ThrowIfNull(part); ArgumentNullException.ThrowIfNull(part.Metric);
            PositiveFinite(part.FontSize, nameof(parts)); Finite(part.BaselineX, nameof(parts)); Finite(part.BaselineY, nameof(parts));
            if (!Enum.IsDefined(part.Role) || part.Role == MathGlyphRole.Regular)
                throw new ArgumentOutOfRangeException(nameof(parts), "A stretched glyph part must have a vertical role.");
            if (part.BaselineX < -1e-7 || part.BaselineY < -1e-7 || part.BaselineY > Height + 1e-7)
                throw new ArgumentException("A vertical glyph part is outside its box.", nameof(parts));
        }
    }

    private static void Finite(double value, string name)
    { if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(name, "Must be finite."); }
    private static void PositiveFinite(double value, string name)
    { Finite(value, name); if (value <= 0) throw new ArgumentOutOfRangeException(name, "Must be positive."); }
}

/// <summary>Optional OpenType MATH capabilities supplied by a platform font adapter.</summary>
public interface IOpenTypeMathMetricsProvider : IMathGlyphMetricsProvider
{
    MathFontConstants GetMathConstants(string fontFamily, double emSize);
    MathVerticalGlyph StretchVertical(string text, string fontFamily, double emSize, double targetHeight);
}

/// <summary>A native-drawable glyph and its baseline origin, without COM/WinUI objects.</summary>
public sealed record MathGlyphPlacement
{
    public required string Text { get; init; }
    public required string FontFamily { get; init; }
    public required string FontFace { get; init; }
    public int GlyphIndex { get; init; }
    public double FontSize { get; init; }
    public double BaselineX { get; init; }
    public double BaselineY { get; init; }
    public double Advance { get; init; }
    public double AdvanceOffset { get; init; }
    public double AscenderOffset { get; init; }
    public MathGlyphRole Role { get; init; }
    public int SourceStart { get; init; }
    public int SourceLength { get; init; }
}

/// <summary>A filled horizontal rule, such as a fraction bar or radical vinculum.</summary>
public sealed record MathRulePlacement
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public int SourceStart { get; init; }
    public int SourceLength { get; init; }
}

/// <summary>Immutable formula box: height is above the baseline and depth is below it.</summary>
public sealed class MathLayoutResult
{
    public string RequestId { get; }
    public string Source { get; }
    public double Width { get; }
    public double Height { get; }
    public double Depth { get; }
    public double Baseline => Height;
    public double TotalHeight => Height + Depth;
    public ImmutableArray<MathGlyphPlacement> Glyphs { get; }
    public ImmutableArray<MathRulePlacement> Rules { get; }

    public MathLayoutResult(string requestId, string source, double width, double height, double depth,
        IEnumerable<MathGlyphPlacement> glyphs, IEnumerable<MathRulePlacement> rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        PositiveFinite(width, nameof(width)); PositiveFinite(height, nameof(height));
        NonnegativeFinite(depth, nameof(depth));
        ArgumentNullException.ThrowIfNull(glyphs); ArgumentNullException.ThrowIfNull(rules);
        RequestId = requestId; Source = source; Width = width; Height = height; Depth = depth;
        Glyphs = glyphs.ToImmutableArray(); Rules = rules.ToImmutableArray();
        if (Glyphs.IsEmpty) throw new ArgumentException("A formula must contain at least one glyph.", nameof(glyphs));
        ValidateGeometry();
    }

    [JsonConstructor]
    public MathLayoutResult(string requestId, string source, double width, double height, double depth,
        ImmutableArray<MathGlyphPlacement> glyphs, ImmutableArray<MathRulePlacement> rules)
        : this(requestId, source, width, height, depth,
            (IEnumerable<MathGlyphPlacement>)glyphs, (IEnumerable<MathRulePlacement>)rules) { }

    private void ValidateGeometry()
    {
        foreach (var glyph in Glyphs)
        {
            ArgumentNullException.ThrowIfNull(glyph);
            ArgumentException.ThrowIfNullOrWhiteSpace(glyph.Text);
            ArgumentException.ThrowIfNullOrWhiteSpace(glyph.FontFamily);
            ArgumentException.ThrowIfNullOrWhiteSpace(glyph.FontFace);
            if (glyph.GlyphIndex is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(Glyphs));
            PositiveFinite(glyph.FontSize, nameof(Glyphs)); NonnegativeFinite(glyph.Advance, nameof(Glyphs));
            Finite(glyph.BaselineX, nameof(Glyphs)); Finite(glyph.BaselineY, nameof(Glyphs));
            Finite(glyph.AdvanceOffset, nameof(Glyphs)); Finite(glyph.AscenderOffset, nameof(Glyphs));
            if (!Enum.IsDefined(glyph.Role)) throw new ArgumentOutOfRangeException(nameof(Glyphs));
            SourceRange(glyph.SourceStart, glyph.SourceLength, nameof(Glyphs));
            if (glyph.BaselineX < -1e-7 || glyph.BaselineX + glyph.Advance > Width + 1e-7 ||
                glyph.BaselineY < -1e-7 || glyph.BaselineY > TotalHeight + 1e-7)
                throw new ArgumentException("Glyph origin is outside the formula box.", nameof(Glyphs));
        }
        foreach (var rule in Rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            Finite(rule.X, nameof(Rules)); Finite(rule.Y, nameof(Rules));
            PositiveFinite(rule.Width, nameof(Rules)); PositiveFinite(rule.Height, nameof(Rules));
            SourceRange(rule.SourceStart, rule.SourceLength, nameof(Rules));
            if (rule.X < -1e-7 || rule.Y < -1e-7 || rule.X + rule.Width > Width + 1e-7 ||
                rule.Y + rule.Height > TotalHeight + 1e-7)
                throw new ArgumentException("Rule is outside the formula box.", nameof(Rules));
        }
    }

    private void SourceRange(int start, int length, string name)
    {
        if (start < 0 || length < 0 || start > Source.Length - length)
            throw new ArgumentOutOfRangeException(name, "Source mapping is outside formula source.");
    }
    private static void Finite(double value, string name)
    { if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(name, "Must be finite."); }
    private static void NonnegativeFinite(double value, string name)
    { Finite(value, name); if (value < 0) throw new ArgumentOutOfRangeException(name, "Must be nonnegative."); }
    private static void PositiveFinite(double value, string name)
    { Finite(value, name); if (value <= 0) throw new ArgumentOutOfRangeException(name, "Must be positive."); }
}

public sealed record MathWorkerResponse
{
    public int ProtocolVersion { get; init; } = MathLayoutRequest.CurrentProtocolVersion;
    public string RequestId { get; init; } = "";
    public bool Success { get; init; }
    public MathLayoutResult? Layout { get; init; }
    public string SessionId { get; init; } = "";
    public long Sequence { get; init; }
    public bool CacheHit { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int? ErrorPosition { get; init; }
}
