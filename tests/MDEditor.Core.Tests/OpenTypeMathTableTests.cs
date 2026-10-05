using System.Buffers.Binary;
using MDEditor.Typesetting.Mathematics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class OpenTypeMathTableTests
{
    [TestMethod]
    public void Reads_constants_format1_coverage_variants_and_assembly()
    {
        var bytes = BuildTable(coverageFormat: 1);
        var table = new OpenTypeMathTable(bytes);
        Assert.AreEqual(80, table.Constants.ScriptPercentScaleDown);
        Assert.AreEqual(60, table.Constants.ScriptScriptPercentScaleDown);
        Assert.AreEqual(900, table.Constants.DisplayOperatorMinHeight);
        Assert.AreEqual(250, table.Constants.AxisHeight);
        Assert.AreEqual(40, table.Constants.FractionRuleThickness);
        Assert.AreEqual(45, table.Constants.RadicalRuleThickness);

        var construction = table.GetVerticalConstruction(100);
        Assert.IsNotNull(construction);
        Assert.AreEqual(20, construction.MinConnectorOverlap);
        CollectionAssert.AreEqual(new ushort[] { 101, 102 }, construction.Variants.Select(item => item.GlyphIndex).ToArray());
        CollectionAssert.AreEqual(new ushort[] { 80, 160 }, construction.Variants.Select(item => item.AdvanceMeasurement).ToArray());
        Assert.AreEqual(2, construction.AssemblyParts.Count);
        Assert.AreEqual(201, construction.AssemblyParts[0].GlyphIndex);
        Assert.IsFalse(construction.AssemblyParts[0].IsExtender);
        Assert.AreEqual(202, construction.AssemblyParts[1].GlyphIndex);
        Assert.IsTrue(construction.AssemblyParts[1].IsExtender);
        Assert.IsNull(table.GetVerticalConstruction(99));
    }

    [TestMethod]
    public void Reads_format2_coverage()
    {
        var table = new OpenTypeMathTable(BuildTable(coverageFormat: 2));
        Assert.IsNotNull(table.GetVerticalConstruction(100));
        Assert.IsNull(table.GetVerticalConstruction(101));
    }

    [TestMethod]
    public void Truncated_or_invalid_tables_are_rejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => new OpenTypeMathTable(new byte[9]));
        var outside = BuildTable(1); WriteU16(outside, 8, ushort.MaxValue);
        Assert.ThrowsExactly<InvalidDataException>(() => new OpenTypeMathTable(outside));
        var reversed = BuildTable(2);
        const int variants = 224; const int coverage = variants + 12;
        WriteU16(reversed, coverage + 4, 101); WriteU16(reversed, coverage + 6, 100);
        var table = new OpenTypeMathTable(reversed);
        Assert.ThrowsExactly<InvalidDataException>(() => table.GetVerticalConstruction(100));
    }

    [TestMethod]
    public void Zero_advance_assembly_part_is_rejected_when_construction_is_read()
    {
        var bytes = BuildTable(1);
        const int variants = 224; const int construction = variants + 18; const int assembly = construction + 12;
        WriteU16(bytes, assembly + 6 + 6, 0);
        var table = new OpenTypeMathTable(bytes);
        Assert.ThrowsExactly<InvalidDataException>(() => table.GetVerticalConstruction(100));
    }

    private static byte[] BuildTable(int coverageFormat)
    {
        const int constants = 10; const int variants = constants + 214;
        const int coverage = variants + 12;
        var construction = variants + (coverageFormat == 1 ? 18 : 22);
        var assembly = construction + 12;
        var data = new byte[assembly + 26];
        WriteU16(data, 0, 1); WriteU16(data, 4, constants); WriteU16(data, 8, variants);
        WriteI16(data, constants, 80); WriteI16(data, constants + 2, 60);
        WriteU16(data, constants + 4, 700); WriteU16(data, constants + 6, 900);
        WriteValue(data, constants, 1, 250); WriteValue(data, constants, 34, 40);
        WriteValue(data, constants, 47, 45);

        WriteU16(data, variants, 20); WriteU16(data, variants + 2, coverage - variants);
        WriteU16(data, variants + 6, 1); WriteU16(data, variants + 10, construction - variants);
        WriteU16(data, coverage, coverageFormat); WriteU16(data, coverage + 2, 1);
        if (coverageFormat == 1) WriteU16(data, coverage + 4, 100);
        else
        {
            WriteU16(data, coverage + 4, 100); WriteU16(data, coverage + 6, 100); WriteU16(data, coverage + 8, 0);
        }

        WriteU16(data, construction, assembly - construction); WriteU16(data, construction + 2, 2);
        WriteU16(data, construction + 4, 101); WriteU16(data, construction + 6, 80);
        WriteU16(data, construction + 8, 102); WriteU16(data, construction + 10, 160);
        WriteU16(data, assembly + 4, 2);
        WritePart(data, assembly + 6, 201, 5, 6, 70, 0);
        WritePart(data, assembly + 16, 202, 10, 10, 50, 1);
        return data;
    }

    private static void WritePart(byte[] data, int offset, ushort glyph, ushort start, ushort end,
        ushort advance, ushort flags)
    {
        WriteU16(data, offset, glyph); WriteU16(data, offset + 2, start); WriteU16(data, offset + 4, end);
        WriteU16(data, offset + 6, advance); WriteU16(data, offset + 8, flags);
    }
    private static void WriteValue(byte[] data, int constants, int index, short value) =>
        WriteI16(data, constants + 8 + index * 4, value);
    private static void WriteU16(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), checked((ushort)value));
    private static void WriteI16(byte[] data, int offset, short value) =>
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(offset, 2), value);
}
