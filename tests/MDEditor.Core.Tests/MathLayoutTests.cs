using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MDEditor.Typesetting.Mathematics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MathLayoutTests
{
    private readonly TeXMathLayoutEngine _engine = new(new PredictableMetrics());

    [TestMethod]
    public void Symbol_returns_width_height_depth_baseline_and_drawable_glyph()
    {
        var result = Layout("x", 40);
        Assert.AreEqual(20, result.Width, 1e-9);
        Assert.AreEqual(30, result.Height, 1e-9);
        Assert.AreEqual(10, result.Depth, 1e-9);
        Assert.AreEqual(result.Height, result.Baseline);
        Assert.AreEqual(1, result.Glyphs.Length);
        Assert.AreEqual("𝑥", result.Glyphs[0].Text);
        Assert.AreEqual(0, result.Glyphs[0].SourceStart);
        Assert.AreEqual(1, result.Glyphs[0].SourceLength);
    }

    [TestMethod]
    public void Fraction_stacks_script_children_and_extracts_one_rule()
    {
        var result = Layout(@"\frac{a+b}{c+d}");
        Assert.AreEqual(6, result.Glyphs.Length);
        Assert.AreEqual(1, result.Rules.Length);
        Assert.IsTrue(result.Depth > 0);
        Assert.IsTrue(result.Glyphs.Take(3).All(glyph => glyph.BaselineY < result.Baseline));
        Assert.IsTrue(result.Glyphs.Skip(3).All(glyph => glyph.BaselineY > result.Baseline));
        Assert.IsTrue(result.Glyphs.All(glyph => glyph.FontSize == 22.4));
    }

    [TestMethod]
    public void Radical_extracts_root_glyph_and_vinculum()
    {
        var result = Layout(@"\sqrt{x^2+y_1}");
        Assert.AreEqual("√", result.Glyphs[0].Text);
        Assert.AreEqual(1, result.Rules.Length);
        Assert.IsTrue(result.Rules[0].X > 0);
        Assert.IsTrue(result.Rules[0].Width > 0);
        Assert.IsTrue(result.Glyphs[0].FontSize >= 32);
    }

    [TestMethod]
    public void Subscript_and_superscript_share_base_and_use_script_style()
    {
        var result = Layout(@"x_i^{n+1}");
        var basis = result.Glyphs.Single(glyph => glyph.SourceStart == 0);
        var sub = result.Glyphs.Single(glyph => glyph.SourceStart == 2);
        var super = result.Glyphs.Single(glyph => glyph.SourceStart == 5);
        Assert.AreEqual(32, basis.FontSize);
        Assert.AreEqual(22.4, sub.FontSize, 1e-9);
        Assert.AreEqual(22.4, super.FontSize, 1e-9);
        Assert.IsTrue(super.BaselineY < basis.BaselineY);
        Assert.IsTrue(sub.BaselineY > basis.BaselineY);
    }

    [TestMethod]
    public void Script_order_is_semantically_equivalent()
    {
        var first = Layout(@"x_i^2");
        var second = Layout(@"x^2_i");
        Assert.AreEqual(first.Width, second.Width, 1e-9);
        Assert.AreEqual(first.Height, second.Height, 1e-9);
        Assert.AreEqual(first.Depth, second.Depth, 1e-9);
    }

    [TestMethod]
    public void Nested_boxes_remain_inside_result_geometry()
    {
        var result = Layout(@"\frac{1}{\sqrt{x_i^2+1}}");
        Assert.IsTrue(result.Rules.Length >= 2);
        AssertGeometry(result);
    }

    [TestMethod]
    [DataRow(MathLayoutStyle.Display, 32d)]
    [DataRow(MathLayoutStyle.Text, 32d)]
    [DataRow(MathLayoutStyle.Script, 22.4d)]
    [DataRow(MathLayoutStyle.ScriptScript, 16d)]
    public void Initial_math_style_controls_em_size(MathLayoutStyle style, double expected)
    {
        var request = Request("x") with { Style = style };
        Assert.AreEqual(expected, _engine.Layout(request).Glyphs[0].FontSize, 1e-9);
    }

    [TestMethod]
    [DataRow(@"\alpha", "α")]
    [DataRow(@"\pi", "π")]
    [DataRow(@"\sum", "∑")]
    [DataRow(@"\int", "∫")]
    [DataRow(@"\infty", "∞")]
    [DataRow(@"\cdot", "·")]
    [DataRow(@"\times", "×")]
    [DataRow(@"\le", "≤")]
    [DataRow(@"\neq", "≠")]
    [DataRow(@"\to", "→")]
    public void Supported_control_sequence_maps_to_drawable_symbol(string source, string expected)
        => Assert.AreEqual(expected, Layout(source).Glyphs.Single().Text);

    [TestMethod]
    [DataRow(@"\unknown{x}")]
    [DataRow(@"\frac{x}")]
    [DataRow(@"\frac{x}y")]
    [DataRow(@"\sqrt x")]
    [DataRow("{x")]
    [DataRow("x}")]
    [DataRow("_x")]
    [DataRow("x__i")]
    [DataRow("x^2^3")]
    [DataRow("\\")]
    public void Malformed_or_unsupported_formula_is_rejected(string source)
        => Assert.Throws<MathParseException>(() => Layout(source));

    [TestMethod]
    public void Whitespace_and_groups_do_not_create_fake_glyphs()
    {
        var result = Layout("  { x + y }  ");
        Assert.AreEqual(3, result.Glyphs.Length);
        CollectionAssert.AreEqual(new[] { 4, 6, 8 }, result.Glyphs.Select(glyph => glyph.SourceStart).ToArray());
    }

    [TestMethod]
    public void Every_primitive_maps_back_into_formula_source()
    {
        var result = Layout(@"\frac{x_i^2}{\sqrt{y+1}}");
        foreach (var glyph in result.Glyphs)
            Assert.IsTrue(glyph.SourceStart >= 0 && glyph.SourceStart + glyph.SourceLength <= result.Source.Length);
        foreach (var rule in result.Rules)
            Assert.IsTrue(rule.SourceStart >= 0 && rule.SourceStart + rule.SourceLength <= result.Source.Length);
    }

    [TestMethod]
    public void Same_request_is_bitwise_deterministic()
    {
        var request = Request(@"\frac{x_i^2+1}{\sqrt{y+1}}");
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var first = JsonSerializer.Serialize(_engine.Layout(request), options);
        var second = JsonSerializer.Serialize(_engine.Layout(request), options);
        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void Result_json_round_trips_without_platform_objects()
    {
        var result = Layout(@"\sqrt{x^2+1}");
        var json = JsonSerializer.Serialize(result);
        var copy = JsonSerializer.Deserialize<MathLayoutResult>(json);
        Assert.IsNotNull(copy);
        Assert.AreEqual(result.Width, copy.Width);
        Assert.AreEqual(result.Glyphs.Length, copy.Glyphs.Length);
        Assert.AreEqual(result.Rules.Length, copy.Rules.Length);
    }

    [TestMethod]
    public void OpenType_math_constants_control_script_scale_and_fraction_rule()
    {
        var engine = new TeXMathLayoutEngine(new AdvancedMetrics());
        var result = engine.Layout(Request(@"\frac{x_i}{y}") with { EmSize = 40 });
        Assert.IsTrue(result.Glyphs.Where(glyph => glyph.Text is "𝑥" or "𝑖" or "𝑦")
            .All(glyph => glyph.FontSize is 32 or 25.6));
        Assert.AreEqual(2, result.Rules.Single().Height, 1e-9);
    }

    [TestMethod]
    public void OpenType_vertical_results_preserve_variant_and_assembly_roles()
    {
        var engine = new TeXMathLayoutEngine(new AdvancedMetrics());
        var displayOperator = engine.Layout(Request(@"\sum_{i=1}^{n}"));
        Assert.AreEqual(MathGlyphRole.VerticalVariant, displayOperator.Glyphs[0].Role);
        var radical = engine.Layout(Request(@"\sqrt{x+1}"));
        Assert.AreEqual(2, radical.Glyphs.Count(glyph => glyph.Role == MathGlyphRole.VerticalAssemblyPart));
        Assert.IsTrue(radical.Glyphs.Where(glyph => glyph.Text == "√")
            .All(glyph => glyph.Role == MathGlyphRole.VerticalAssemblyPart));
    }

    [TestMethod]
    public void Radical_rule_uses_the_font_connector_instead_of_the_assembly_box_edge()
    {
        var result = new TeXMathLayoutEngine(new AdvancedMetrics()).Layout(Request(@"\sqrt{x+1}"));
        var extra = 32 * .05;
        Assert.AreEqual(extra, result.Rules.Single().Y, 1e-9);
        Assert.AreEqual(7 - result.Rules.Single().Height * .5, result.Rules.Single().X, 1e-9);
        CollectionAssert.AreEqual(new[] { 22 + extra - 6, 90 + extra - 6 }, result.Glyphs
            .Where(glyph => glyph.Text == "√").Select(glyph => glyph.BaselineY).ToArray());
    }

    [TestMethod]
    public void Vertical_glyph_rejects_connector_anchors_outside_its_box()
    {
        var part = new MathVerticalGlyphPart
        {
            Metric = AdvancedMetrics.Metric("√", "Test Math", 32, 8), FontSize = 32,
            BaselineY = 20, Role = MathGlyphRole.VerticalVariant
        };
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MathVerticalGlyph(8, 24, [part], 25, 7));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MathVerticalGlyph(8, 24, [part], 4, 9));
    }

    [TestMethod]
    public void Math_glyph_roles_round_trip_as_portable_json()
    {
        var result = new TeXMathLayoutEngine(new AdvancedMetrics()).Layout(Request(@"\sqrt{x}"));
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        var copy = JsonSerializer.Deserialize<MathLayoutResult>(JsonSerializer.Serialize(result, options), options);
        Assert.IsNotNull(copy);
        CollectionAssert.AreEqual(result.Glyphs.Select(glyph => glyph.Role).ToArray(),
            copy.Glyphs.Select(glyph => glyph.Role).ToArray());
    }

    [TestMethod]
    [DataRow(0, "id", "x", 32, "Cambria Math")]
    [DataRow(1, "", "x", 32, "Cambria Math")]
    [DataRow(1, "id", "", 32, "Cambria Math")]
    [DataRow(1, "id", "x", 5, "Cambria Math")]
    [DataRow(1, "id", "x", 513, "Cambria Math")]
    [DataRow(1, "id", "x", 32, "")]
    public void Invalid_request_contract_is_rejected(int version, string id, string source, double em, string family)
    {
        var request = new MathLayoutRequest
        { ProtocolVersion = version, RequestId = id, Source = source, EmSize = em, FontFamily = family };
        Assert.Throws<ArgumentException>(request.Validate);
    }

    [TestMethod]
    public void Caller_mutation_cannot_change_result_collections()
    {
        var glyphs = new List<MathGlyphPlacement> { Glyph() };
        var rules = new List<MathRulePlacement>();
        var result = new MathLayoutResult("id", "x", 10, 8, 2, glyphs, rules);
        glyphs.Clear(); rules.Add(new MathRulePlacement { X = 0, Y = 0, Width = 1, Height = 1 });
        Assert.AreEqual(1, result.Glyphs.Length);
        Assert.AreEqual(0, result.Rules.Length);
    }

    [TestMethod]
    public void Out_of_bounds_primitive_is_rejected()
    {
        var glyph = Glyph() with { BaselineX = 9, Advance = 2 };
        Assert.ThrowsExactly<ArgumentException>(() => new MathLayoutResult("id", "x", 10, 8, 2, [glyph], []));
    }

    [TestMethod]
    public void Invalid_metric_provider_is_rejected()
    {
        var engine = new TeXMathLayoutEngine(new InvalidMetrics());
        Assert.ThrowsExactly<InvalidOperationException>(() => engine.Layout(Request("x")));
    }

    private MathLayoutResult Layout(string source, double em = 32) => _engine.Layout(Request(source) with { EmSize = em });
    private static MathLayoutRequest Request(string source) => new() { RequestId = "test", Source = source };

    private static void AssertGeometry(MathLayoutResult result)
    {
        Assert.AreEqual(result.Height, result.Baseline, 1e-12);
        Assert.IsTrue(result.Glyphs.All(glyph => glyph.BaselineX >= 0 && glyph.BaselineX + glyph.Advance <= result.Width + 1e-9));
        Assert.IsTrue(result.Glyphs.All(glyph => glyph.BaselineY >= 0 && glyph.BaselineY <= result.TotalHeight + 1e-9));
        Assert.IsTrue(result.Rules.All(rule => rule.X >= 0 && rule.Y >= 0 && rule.X + rule.Width <= result.Width + 1e-9 && rule.Y + rule.Height <= result.TotalHeight + 1e-9));
    }

    private static MathGlyphPlacement Glyph() => new()
    {
        Text = "x", FontFamily = "Test Math", FontFace = "Regular", GlyphIndex = 1,
        FontSize = 10, BaselineX = 0, BaselineY = 8, Advance = 5, SourceStart = 0, SourceLength = 1
    };

    private sealed class PredictableMetrics : IMathGlyphMetricsProvider
    {
        public MathGlyphMetric Measure(string text, string fontFamily, double emSize)
        {
            var scalar = text.EnumerateRunes().Single().Value;
            return new MathGlyphMetric
            {
                Text = text, FontFamily = fontFamily, FontFace = "Regular",
                GlyphIndex = scalar % 60000 + 1, Advance = emSize * 0.5,
                Ascent = emSize * 0.75, Descent = emSize * 0.25
            };
        }
    }

    private sealed class InvalidMetrics : IMathGlyphMetricsProvider
    {
        public MathGlyphMetric Measure(string text, string fontFamily, double emSize) => new()
        { Text = text, FontFamily = fontFamily, FontFace = "Regular", GlyphIndex = -1, Advance = double.NaN, Ascent = 0 };
    }

    private sealed class AdvancedMetrics : IOpenTypeMathMetricsProvider
    {
        public MathGlyphMetric Measure(string text, string fontFamily, double emSize) => Metric(text, fontFamily, emSize);

        public MathFontConstants GetMathConstants(string fontFamily, double emSize) => new()
        {
            ScriptScale = .8, ScriptScriptScale = .64, DisplayOperatorMinHeight = emSize * 1.8,
            AxisHeight = emSize * .25, SubscriptShiftDown = emSize * .3, SubscriptTopMax = emSize * .4,
            SuperscriptShiftUp = emSize * .5, SuperscriptBottomMin = emSize * .2,
            SubSuperscriptGapMin = emSize * .2, SpaceAfterScript = emSize * .04,
            FractionNumeratorShiftUp = emSize * .55, FractionNumeratorDisplayStyleShiftUp = emSize * .7,
            FractionDenominatorShiftDown = emSize * .55, FractionDenominatorDisplayStyleShiftDown = emSize * .7,
            FractionNumeratorGapMin = emSize * .12, FractionNumeratorDisplayStyleGapMin = emSize * .18,
            FractionRuleThickness = emSize * .05, FractionDenominatorGapMin = emSize * .12,
            FractionDenominatorDisplayStyleGapMin = emSize * .18, RadicalVerticalGap = emSize * .1,
            RadicalDisplayStyleVerticalGap = emSize * .14, RadicalRuleThickness = emSize * .05,
            RadicalExtraAscender = emSize * .05
        };

        public MathVerticalGlyph StretchVertical(string text, string fontFamily, double emSize, double targetHeight)
        {
            if (text == "√")
                return new(8, 100,
                [
                    new MathVerticalGlyphPart { Metric = Metric(text, fontFamily, emSize, 8), FontSize = emSize,
                        BaselineY = 22, Role = MathGlyphRole.VerticalAssemblyPart },
                    new MathVerticalGlyphPart { Metric = Metric(text, fontFamily, emSize, 8), FontSize = emSize,
                        BaselineY = 90, Role = MathGlyphRole.VerticalAssemblyPart }
                ], topConnectorY: 6, topConnectorEndX: 7);
            return new(16, targetHeight,
            [
                new MathVerticalGlyphPart { Metric = Metric(text, fontFamily, emSize, 16), FontSize = emSize,
                    BaselineY = targetHeight * .75, Role = MathGlyphRole.VerticalVariant }
            ]);
        }

        internal static MathGlyphMetric Metric(string text, string family, double em, double? advance = null) => new()
        {
            Text = text, FontFamily = family, FontFace = "Regular",
            GlyphIndex = text.EnumerateRunes().Single().Value % 60000 + 1,
            Advance = advance ?? em * .5, Ascent = em * .75, Descent = em * .25
        };
    }
}
