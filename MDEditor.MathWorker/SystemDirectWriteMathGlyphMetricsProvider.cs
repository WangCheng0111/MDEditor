using System.Runtime.InteropServices;
using System.Text;
using MDEditor.Typesetting.Mathematics;

namespace MDEditor.MathWorker;

/// <summary>Minimal system DirectWrite adapter: no WinUI/Win2D runtime is loaded in the worker.</summary>
internal sealed class SystemDirectWriteMathGlyphMetricsProvider : IOpenTypeMathMetricsProvider, IDisposable
{
    private readonly IDWriteFactory _factory;
    private readonly IDWriteFontCollection _collection;
    private readonly IDWriteFontFamily _family;
    private readonly IDWriteFont _font;
    private readonly IDWriteFontFace _face;
    private readonly string _familyName;
    private readonly DWriteFontMetrics _fontMetrics;
    private readonly OpenTypeMathTable _math;
    private bool _disposed;

    public SystemDirectWriteMathGlyphMetricsProvider(string familyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);
        var iid = typeof(IDWriteFactory).GUID;
        Check(DWriteCreateFactory(0, ref iid, out var factoryObject));
        _factory = (IDWriteFactory)factoryObject;
        Check(_factory.GetSystemFontCollection(out _collection, false));
        Check(_collection.FindFamilyName(familyName, out var familyIndex, out var exists));
        if (!exists) throw new NotSupportedException($"Math font '{familyName}' is not installed.");
        Check(_collection.GetFontFamily(familyIndex, out _family));
        Check(_family.GetFirstMatchingFont(400, 5, 0, out _font));
        Check(_font.CreateFontFace(out _face));
        _familyName = familyName;
        _fontMetrics = ReadFontMetrics();
        if (_fontMetrics.DesignUnitsPerEm == 0) throw new InvalidOperationException("DirectWrite returned zero design units per em.");
        _math = ReadMathTable();
    }

    public unsafe MathGlyphMetric Measure(string text, string fontFamily, double emSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(fontFamily, _familyName, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"This worker instance is bound to '{_familyName}'.");
        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length != 1) throw new NotSupportedException("A math atom must contain exactly one Unicode scalar.");
        if (!double.IsFinite(emSize) || emSize <= 0 || emSize > 2048) throw new ArgumentOutOfRangeException(nameof(emSize));
        var codePoint = checked((uint)runes[0].Value);
        GetGlyphData(codePoint, out var glyphIndex, out var glyphMetric);
        if (glyphIndex == 0) throw new NotSupportedException($"'{_familyName}' has no glyph for U+{codePoint:X4}.");
        return Metric(text, glyphIndex, glyphMetric, emSize);
    }

    public MathFontConstants GetMathConstants(string fontFamily, double emSize)
    {
        Validate(fontFamily, emSize); var raw = _math.Constants; var scale = emSize / _fontMetrics.DesignUnitsPerEm;
        double S(short value) => Math.Max(0, value * scale);
        return new()
        {
            ScriptScale = Math.Clamp(raw.ScriptPercentScaleDown / 100d, .01, 1),
            ScriptScriptScale = Math.Clamp(raw.ScriptScriptPercentScaleDown / 100d, .01, 1),
            DisplayOperatorMinHeight = raw.DisplayOperatorMinHeight * scale,
            AxisHeight = S(raw.AxisHeight), SubscriptShiftDown = S(raw.SubscriptShiftDown),
            SubscriptTopMax = S(raw.SubscriptTopMax), SuperscriptShiftUp = S(raw.SuperscriptShiftUp),
            SuperscriptBottomMin = S(raw.SuperscriptBottomMin), SubSuperscriptGapMin = S(raw.SubSuperscriptGapMin),
            SpaceAfterScript = S(raw.SpaceAfterScript), FractionNumeratorShiftUp = S(raw.FractionNumeratorShiftUp),
            FractionNumeratorDisplayStyleShiftUp = S(raw.FractionNumeratorDisplayStyleShiftUp),
            FractionDenominatorShiftDown = S(raw.FractionDenominatorShiftDown),
            FractionDenominatorDisplayStyleShiftDown = S(raw.FractionDenominatorDisplayStyleShiftDown),
            FractionNumeratorGapMin = S(raw.FractionNumeratorGapMin),
            FractionNumeratorDisplayStyleGapMin = S(raw.FractionNumeratorDisplayStyleGapMin),
            FractionRuleThickness = S(raw.FractionRuleThickness),
            FractionDenominatorGapMin = S(raw.FractionDenominatorGapMin),
            FractionDenominatorDisplayStyleGapMin = S(raw.FractionDenominatorDisplayStyleGapMin),
            RadicalVerticalGap = S(raw.RadicalVerticalGap),
            RadicalDisplayStyleVerticalGap = S(raw.RadicalDisplayStyleVerticalGap),
            RadicalRuleThickness = S(raw.RadicalRuleThickness), RadicalExtraAscender = S(raw.RadicalExtraAscender)
        };
    }

    public MathVerticalGlyph StretchVertical(string text, string fontFamily, double emSize, double targetHeight)
    {
        Validate(fontFamily, emSize);
        if (!double.IsFinite(targetHeight) || targetHeight <= 0 || targetHeight > 32768)
            throw new ArgumentOutOfRangeException(nameof(targetHeight));
        var rune = text.EnumerateRunes().SingleOrDefault();
        if (rune.Value == 0 && text != "\0") throw new NotSupportedException("A stretchy atom must contain one Unicode scalar.");
        GetGlyphData(checked((uint)rune.Value), out var baseGlyph, out _);
        if (baseGlyph == 0) throw new NotSupportedException($"'{_familyName}' has no glyph for '{text}'.");
        var construction = _math.GetVerticalConstruction(baseGlyph);
        var scale = emSize / _fontMetrics.DesignUnitsPerEm;
        var targetDesign = checked((int)Math.Ceiling(targetHeight / scale));
        if (construction is not null)
        {
            var variant = construction.Variants.FirstOrDefault(item => item.AdvanceMeasurement >= targetDesign);
            if (variant is not null) return Variant(text, variant.GlyphIndex, variant.AdvanceMeasurement, emSize);
            if (construction.AssemblyParts.Count > 0)
                return Assembly(text, construction, targetDesign, emSize);
            var largest = construction.Variants.LastOrDefault();
            if (largest is not null) return Variant(text, largest.GlyphIndex, largest.AdvanceMeasurement, emSize);
        }
        var regular = Measure(text, fontFamily, emSize); var actual = regular.Ascent + regular.Descent;
        var fallbackSize = actual >= targetHeight ? emSize : Math.Min(emSize * 8, emSize * targetHeight / actual);
        regular = Measure(text, fontFamily, fallbackSize);
        return new(Math.Max(regular.Advance, fallbackSize * .02), regular.Ascent + regular.Descent,
            [new MathVerticalGlyphPart { Metric = regular, FontSize = fallbackSize, BaselineY = regular.Ascent,
                Role = MathGlyphRole.VerticalVariant }]);
    }

    private MathVerticalGlyph Variant(string text, ushort glyph, ushort advance, double emSize)
    {
        var design = GetDesignMetrics(glyph); var scale = emSize / _fontMetrics.DesignUnitsPerEm;
        var metric = Metric(text, glyph, design, emSize);
        var inkLeft = design.LeftSideBearing * scale;
        var inkRight = ((double)design.AdvanceWidth - design.RightSideBearing) * scale;
        var inkTop = design.TopSideBearing * scale;
        var inkHeight = Math.Max(0, (double)design.AdvanceHeight - design.TopSideBearing - design.BottomSideBearing) * scale;
        var rawHeight = advance * scale; var minY = Math.Min(0, inkTop); var maxY = Math.Max(rawHeight, inkTop + inkHeight);
        var rawWidth = Math.Max(metric.Advance, emSize * .02);
        var minX = Math.Min(0, inkLeft); var maxX = Math.Max(rawWidth, inkRight);
        return new(maxX - minX, maxY - minY,
            [new MathVerticalGlyphPart { Metric = metric, FontSize = emSize,
                BaselineX = -minX, BaselineY = design.VerticalOriginY * scale - minY,
                Role = MathGlyphRole.VerticalVariant }],
            inkTop - minY, inkRight - minX);
    }

    private MathVerticalGlyph Assembly(string text, OpenTypeMathConstruction construction, int target, double emSize)
    {
        var built = BuildAssembly(construction, target); var scale = emSize / _fontMetrics.DesignUnitsPerEm;
        var drafts = new List<(MathGlyphMetric Metric, double BaselineY, double InkLeft, double InkRight, double InkTop)>();
        double minY = 0, maxY = built.Height * scale, baseWidth = 0;
        foreach (var item in built.Parts)
        {
            var design = GetDesignMetrics(item.Part.GlyphIndex); var metric = Metric(text, item.Part.GlyphIndex, design, emSize);
            var baseline = item.Top * scale + design.VerticalOriginY * scale;
            var inkLeft = design.LeftSideBearing * scale;
            var inkRight = ((double)design.AdvanceWidth - design.RightSideBearing) * scale;
            var inkTop = item.Top * scale + design.TopSideBearing * scale;
            var inkHeight = Math.Max(0, (double)design.AdvanceHeight - design.TopSideBearing - design.BottomSideBearing) * scale;
            minY = Math.Min(minY, inkTop); maxY = Math.Max(maxY, inkTop + inkHeight);
            baseWidth = Math.Max(baseWidth, metric.Advance);
            drafts.Add((metric, baseline, inkLeft, inkRight, inkTop));
        }
        baseWidth = Math.Max(baseWidth, emSize * .02);
        double minX = 0, maxX = baseWidth;
        foreach (var item in drafts)
        {
            var origin = (baseWidth - item.Metric.Advance) / 2;
            minX = Math.Min(minX, origin + item.InkLeft);
            maxX = Math.Max(maxX, origin + item.InkRight);
        }
        var shiftX = -minX; var shiftY = -minY; var width = maxX - minX;
        var top = drafts[0]; var topOrigin = (baseWidth - top.Metric.Advance) / 2 + shiftX;
        return new(width, maxY - minY, drafts.Select(item => new MathVerticalGlyphPart
        {
            Metric = item.Metric, FontSize = emSize,
            BaselineX = (baseWidth - item.Metric.Advance) / 2 + shiftX,
            BaselineY = item.BaselineY + shiftY, Role = MathGlyphRole.VerticalAssemblyPart
        }), top.InkTop + shiftY, topOrigin + top.InkRight);
    }

    private static (IReadOnlyList<(OpenTypeMathAssemblyPart Part, double Top)> Parts, double Height) BuildAssembly(
        OpenTypeMathConstruction construction, int target)
    {
        var source = construction.AssemblyParts; var hasExtender = source.Any(part => part.IsExtender);
        List<OpenTypeMathAssemblyPart>? selected = null; double[]? overlaps = null; double height = 0;
        for (var repeats = 0; repeats <= 128; repeats++)
        {
            var bottomUp = source.SelectMany(part => Enumerable.Repeat(part, part.IsExtender ? repeats : 1)).ToList();
            if (bottomUp.Count == 0) continue;
            var maximum = new double[Math.Max(0, bottomUp.Count - 1)]; var minimum = new double[maximum.Length];
            for (var i = 0; i < maximum.Length; i++)
            {
                maximum[i] = Math.Min(bottomUp[i].EndConnectorLength, bottomUp[i + 1].StartConnectorLength);
                minimum[i] = maximum[i] == 0 ? 0 : Math.Min(construction.MinConnectorOverlap, maximum[i]);
            }
            var sum = bottomUp.Sum(part => (double)part.FullAdvance);
            var smallest = sum - maximum.Sum(); var largest = sum - minimum.Sum();
            if (target <= largest || !hasExtender || repeats == 128)
            {
                var wanted = Math.Clamp(target, smallest, largest); var capacity = maximum.Zip(minimum, (a, b) => a - b).Sum();
                var ratio = capacity == 0 ? 0 : (wanted - smallest) / capacity;
                overlaps = maximum.Zip(minimum, (a, b) => a - (a - b) * ratio).ToArray();
                selected = bottomUp; height = sum - overlaps.Sum(); break;
            }
        }
        if (selected is null || overlaps is null) throw new InvalidOperationException("OpenType MATH assembly could not be constructed.");
        var bottomOffsets = new double[selected.Count];
        for (var i = 1; i < selected.Count; i++) bottomOffsets[i] = bottomOffsets[i - 1] + selected[i - 1].FullAdvance - overlaps[i - 1];
        var result = selected.Select((part, index) => (part, Top: height - bottomOffsets[index] - part.FullAdvance))
            .OrderBy(item => item.Top).ToArray();
        return (Array.AsReadOnly(result), height);
    }

    private void Validate(string fontFamily, double emSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(fontFamily, _familyName, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"This worker instance is bound to '{_familyName}'.");
        if (!double.IsFinite(emSize) || emSize <= 0 || emSize > 2048) throw new ArgumentOutOfRangeException(nameof(emSize));
    }

    private MathGlyphMetric Metric(string text, ushort glyph, DWriteGlyphMetrics design, double emSize)
    {
        var scale = emSize / _fontMetrics.DesignUnitsPerEm;
        return new() { Text = text, FontFamily = _familyName, FontFace = "Regular", GlyphIndex = glyph,
            Advance = design.AdvanceWidth * scale, Ascent = _fontMetrics.Ascent * scale, Descent = _fontMetrics.Descent * scale };
    }

    private unsafe void GetGlyphData(uint codePoint, out ushort glyphIndex, out DWriteGlyphMetrics glyphMetric)
    {
        glyphIndex = 0; glyphMetric = default;
        var facePointer = Marshal.GetIUnknownForObject(_face);
        try
        {
            var table = *(IntPtr**)facePointer;
            var getDesignMetrics = (delegate* unmanaged[Stdcall]<IntPtr, ushort*, uint, DWriteGlyphMetrics*, int, int>)table[10];
            var getGlyphIndices = (delegate* unmanaged[Stdcall]<IntPtr, uint*, uint, ushort*, int>)table[11];
            fixed (ushort* glyphPointer = &glyphIndex)
            fixed (DWriteGlyphMetrics* glyphMetricPointer = &glyphMetric)
            {
                Check(getGlyphIndices(facePointer, &codePoint, 1, glyphPointer));
                Check(getDesignMetrics(facePointer, glyphPointer, 1, glyphMetricPointer, 0));
            }
        }
        finally { Marshal.Release(facePointer); }
    }

    private unsafe DWriteGlyphMetrics GetDesignMetrics(ushort glyphIndex)
    {
        var metric = default(DWriteGlyphMetrics); var facePointer = Marshal.GetIUnknownForObject(_face);
        try
        {
            var table = *(IntPtr**)facePointer;
            var get = (delegate* unmanaged[Stdcall]<IntPtr, ushort*, uint, DWriteGlyphMetrics*, int, int>)table[10];
            Check(get(facePointer, &glyphIndex, 1, &metric, 0)); return metric;
        }
        finally { Marshal.Release(facePointer); }
    }

    private unsafe DWriteFontMetrics ReadFontMetrics()
    {
        var metrics = default(DWriteFontMetrics); var facePointer = Marshal.GetIUnknownForObject(_face);
        try
        {
            var table = *(IntPtr**)facePointer;
            var get = (delegate* unmanaged[Stdcall]<IntPtr, DWriteFontMetrics*, void>)table[8];
            get(facePointer, &metrics); return metrics;
        }
        finally { Marshal.Release(facePointer); }
    }

    private unsafe OpenTypeMathTable ReadMathTable()
    {
        var facePointer = Marshal.GetIUnknownForObject(_face); void* data = null; uint size = 0; void* context = null; int exists = 0;
        try
        {
            var table = *(IntPtr**)facePointer;
            var get = (delegate* unmanaged[Stdcall]<IntPtr, uint, void**, uint*, void**, int*, int>)table[12];
            var release = (delegate* unmanaged[Stdcall]<IntPtr, void*, void>)table[13];
            const uint mathTag = (uint)'M' | ((uint)'A' << 8) | ((uint)'T' << 16) | ((uint)'H' << 24);
            Check(get(facePointer, mathTag, &data, &size, &context, &exists));
            if (exists == 0 || data is null || size == 0) throw new NotSupportedException($"'{_familyName}' has no OpenType MATH table.");
            try { return new(new ReadOnlySpan<byte>(data, checked((int)size))); }
            finally { release(facePointer, context); }
        }
        finally { Marshal.Release(facePointer); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Release(_face); Release(_font); Release(_family); Release(_collection); Release(_factory);
    }

    private static void Check(int hresult) { if (hresult < 0) Marshal.ThrowExceptionForHR(hresult); }
    private static void Release(object value) { if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    [DllImport("dwrite.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int DWriteCreateFactory(int factoryType, ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [ComImport, Guid("B859EE5A-D838-4B5B-A2E8-1ADC7D93DB48"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFactory
    {
        [PreserveSig] int GetSystemFontCollection(out IDWriteFontCollection fontCollection,
            [MarshalAs(UnmanagedType.Bool)] bool checkForUpdates);
    }

    [ComImport, Guid("A84CEE02-3EEA-4EEE-A827-87C1A02A0FCC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontCollection
    {
        uint GetFontFamilyCount();
        [PreserveSig] int GetFontFamily(uint index, out IDWriteFontFamily fontFamily);
        [PreserveSig] int FindFamilyName([MarshalAs(UnmanagedType.LPWStr)] string familyName, out uint index,
            [MarshalAs(UnmanagedType.Bool)] out bool exists);
        [PreserveSig] int GetFontFromFontFace(IDWriteFontFace fontFace, out IDWriteFont font);
    }

    [ComImport, Guid("DA20D8EF-812A-4C43-9802-62EC4ABD7ADF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontFamily
    {
        // Flatten IDWriteFontList before the family-specific slots; RCW interface inheritance is not vtable inheritance.
        [PreserveSig] int GetFontCollection(out IDWriteFontCollection fontCollection);
        uint GetFontCount();
        [PreserveSig] int GetFont(uint index, out IDWriteFont font);
        [PreserveSig] int GetFamilyNames(out IntPtr names);
        [PreserveSig] int GetFirstMatchingFont(int weight, int stretch, int style, out IDWriteFont matchingFont);
        [PreserveSig] int GetMatchingFonts(int weight, int stretch, int style, out IntPtr matchingFonts);
    }

    [ComImport, Guid("ACD16696-8C14-4F5D-877E-FE3FC1D32737"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFont
    {
        void GetFontFamily(out IDWriteFontFamily fontFamily);
        int GetWeight();
        int GetStretch();
        int GetStyle();
        [return: MarshalAs(UnmanagedType.Bool)] bool IsSymbolFont();
        [PreserveSig] int GetFaceNames(out IntPtr names);
        [PreserveSig] int GetInformationalStrings(int informationalStringId, out IntPtr informationalStrings,
            [MarshalAs(UnmanagedType.Bool)] out bool exists);
        int GetSimulations();
        void GetMetrics(out DWriteFontMetrics fontMetrics);
        [return: MarshalAs(UnmanagedType.Bool)] bool HasCharacter(uint unicodeValue);
        [PreserveSig] int CreateFontFace(out IDWriteFontFace fontFace);
    }

    [ComImport, Guid("5F49804D-7024-4D43-BFA9-D25984F53849"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontFace
    {
        int GetFaceType();
        [PreserveSig] int GetFiles(ref uint numberOfFiles, IntPtr fontFiles);
        uint GetIndex();
        int GetSimulations();
        [return: MarshalAs(UnmanagedType.Bool)] bool IsSymbolFont();
        void GetMetrics(out DWriteFontMetrics fontMetrics);
        ushort GetGlyphCount();
        [PreserveSig] int GetDesignGlyphMetrics(IntPtr glyphIndices, uint glyphCount, IntPtr glyphMetrics,
            [MarshalAs(UnmanagedType.Bool)] bool isSideways);
        [PreserveSig] int GetGlyphIndices(IntPtr codePoints, uint codePointCount, IntPtr glyphIndices);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWriteFontMetrics
    {
        public ushort DesignUnitsPerEm, Ascent, Descent;
        public short LineGap;
        public ushort CapHeight, XHeight;
        public short UnderlinePosition;
        public ushort UnderlineThickness;
        public short StrikethroughPosition;
        public ushort StrikethroughThickness;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWriteGlyphMetrics
    {
        public int LeftSideBearing;
        public uint AdvanceWidth;
        public int RightSideBearing, TopSideBearing;
        public uint AdvanceHeight;
        public int BottomSideBearing, VerticalOriginY;
    }
}
