using System.Buffers.Binary;

namespace MDEditor.Typesetting.Mathematics;

public sealed record OpenTypeMathConstants(
    short ScriptPercentScaleDown, short ScriptScriptPercentScaleDown,
    ushort DelimitedSubFormulaMinHeight, ushort DisplayOperatorMinHeight,
    short AxisHeight, short SubscriptShiftDown, short SubscriptTopMax,
    short SuperscriptShiftUp, short SuperscriptBottomMin, short SubSuperscriptGapMin,
    short SpaceAfterScript, short FractionNumeratorShiftUp,
    short FractionNumeratorDisplayStyleShiftUp, short FractionDenominatorShiftDown,
    short FractionDenominatorDisplayStyleShiftDown, short FractionNumeratorGapMin,
    short FractionNumeratorDisplayStyleGapMin, short FractionRuleThickness,
    short FractionDenominatorGapMin, short FractionDenominatorDisplayStyleGapMin,
    short RadicalVerticalGap, short RadicalDisplayStyleVerticalGap,
    short RadicalRuleThickness, short RadicalExtraAscender);

public sealed record OpenTypeMathVariant(ushort GlyphIndex, ushort AdvanceMeasurement);
public sealed record OpenTypeMathAssemblyPart(ushort GlyphIndex, ushort StartConnectorLength,
    ushort EndConnectorLength, ushort FullAdvance, bool IsExtender);
public sealed record OpenTypeMathConstruction(IReadOnlyList<OpenTypeMathVariant> Variants,
    IReadOnlyList<OpenTypeMathAssemblyPart> AssemblyParts, ushort MinConnectorOverlap);

/// <summary>Bounds-checked reader for the MATH constants and vertical-variant structures.</summary>
public sealed class OpenTypeMathTable
{
    private const int MathValueSize = 4;
    private readonly byte[] _data;
    private readonly int _constants;
    private readonly int _variants;

    public OpenTypeMathTable(ReadOnlySpan<byte> table)
    {
        if (table.Length < 10) throw new InvalidDataException("The OpenType MATH table is truncated.");
        _data = table.ToArray();
        if (U16(0) != 1) throw new InvalidDataException("Unsupported OpenType MATH major version.");
        _constants = Offset(0, U16(4), 214, "MathConstants");
        _variants = Offset(0, U16(8), 10, "MathVariants");
        _ = Constants;
    }

    public OpenTypeMathConstants Constants => new(
        I16(_constants), I16(_constants + 2), U16(_constants + 4), U16(_constants + 6),
        Value(1), Value(4), Value(5), Value(7), Value(9), Value(11), Value(13),
        Value(28), Value(29), Value(30), Value(31), Value(32), Value(33), Value(34),
        Value(35), Value(36), Value(45), Value(46), Value(47), Value(48));

    public OpenTypeMathConstruction? GetVerticalConstruction(ushort glyphIndex)
    {
        var count = U16(_variants + 6);
        if (count > 4096) throw new InvalidDataException("OpenType MATH vertical construction count is unreasonable.");
        var coverageOffset = U16(_variants + 2);
        if (coverageOffset == 0) return null;
        var index = CoverageIndex(Offset(_variants, coverageOffset, 4, "vertical coverage"), glyphIndex);
        if (index < 0) return null;
        if (index >= count) throw new InvalidDataException("OpenType MATH coverage exceeds construction count.");
        Ensure(_variants + 10, checked(count * 2), "vertical construction offsets");
        var construction = Offset(_variants, U16(_variants + 10 + index * 2), 4, "vertical construction");
        var assemblyOffset = U16(construction);
        var variantCount = U16(construction + 2);
        if (variantCount > 4096) throw new InvalidDataException("OpenType MATH variant count is unreasonable.");
        Ensure(construction + 4, checked(variantCount * 4), "vertical variants");
        var variants = new OpenTypeMathVariant[variantCount];
        for (var i = 0; i < variants.Length; i++)
            variants[i] = new(U16(construction + 4 + i * 4), U16(construction + 6 + i * 4));

        var parts = Array.Empty<OpenTypeMathAssemblyPart>();
        if (assemblyOffset != 0)
        {
            var assembly = Offset(construction, assemblyOffset, 6, "glyph assembly");
            var partCount = U16(assembly + 4);
            if (partCount > 4096) throw new InvalidDataException("OpenType MATH assembly part count is unreasonable.");
            Ensure(assembly + 6, checked(partCount * 10), "glyph assembly parts");
            parts = new OpenTypeMathAssemblyPart[partCount];
            for (var i = 0; i < parts.Length; i++)
            {
                var p = assembly + 6 + i * 10;
                parts[i] = new(U16(p), U16(p + 2), U16(p + 4), U16(p + 6), (U16(p + 8) & 1) != 0);
                if (parts[i].FullAdvance == 0) throw new InvalidDataException("An OpenType MATH assembly part has zero advance.");
            }
        }
        return new(Array.AsReadOnly(variants), Array.AsReadOnly(parts), U16(_variants));
    }

    private short Value(int index) => I16(_constants + 8 + checked(index * MathValueSize));

    private int CoverageIndex(int offset, ushort glyph)
    {
        var format = U16(offset);
        var count = U16(offset + 2);
        if (count > 8192) throw new InvalidDataException("OpenType coverage count is unreasonable.");
        if (format == 1)
        {
            Ensure(offset + 4, checked(count * 2), "coverage glyphs");
            for (var i = 0; i < count; i++) if (U16(offset + 4 + i * 2) == glyph) return i;
            return -1;
        }
        if (format == 2)
        {
            Ensure(offset + 4, checked(count * 6), "coverage ranges");
            for (var i = 0; i < count; i++)
            {
                var p = offset + 4 + i * 6;
                var start = U16(p); var end = U16(p + 2); var first = U16(p + 4);
                if (start > end) throw new InvalidDataException("OpenType coverage range is reversed.");
                if (glyph >= start && glyph <= end) return checked(first + glyph - start);
            }
            return -1;
        }
        throw new InvalidDataException($"Unsupported OpenType coverage format {format}.");
    }

    private int Offset(int origin, ushort relative, int required, string name)
    {
        if (relative == 0) throw new InvalidDataException($"OpenType MATH {name} offset is null.");
        var result = checked(origin + relative); Ensure(result, required, name); return result;
    }
    private ushort U16(int offset) { Ensure(offset, 2, "uint16"); return BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset, 2)); }
    private short I16(int offset) { Ensure(offset, 2, "int16"); return BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(offset, 2)); }
    private void Ensure(int offset, int length, string name)
    {
        if (offset < 0 || length < 0 || offset > _data.Length - length)
            throw new InvalidDataException($"OpenType MATH {name} is outside the table.");
    }
}
