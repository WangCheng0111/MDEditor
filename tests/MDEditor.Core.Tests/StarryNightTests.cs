using System.Collections.Immutable;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Services;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class StarryNightMappingTests
{
    [TestMethod]
    public void Quoted_crlf_code_and_unicode_keep_exact_source_offsets()
    {
        var source = new SourceTextSnapshot("> ```python\r\n> \"中文😀\"\r\n> # café\r\n> ```", 12);
        var structure = MarkdownCodeBlockStructure.Create(MarkdownSyntaxParser.Parse(source));
        var input = MarkdownCodeHighlighter.CreateInputs(structure).Blocks.Single();
        Assert.AreEqual("\"中文😀\"\r\n# café\r\n", input.Text);
        var mapped = MarkdownCodeHighlighter.MapTokens(input,
            [new(new(0, input.Text.Length), MarkdownCodeTokenKind.Comment)]);
        Assert.HasCount(2, mapped);
        Assert.AreEqual("\"中文😀\"", source.GetText(mapped[0].Source));
        Assert.AreEqual("# café", source.GetText(mapped[1].Source));
        var highlight = new MarkdownCodeHighlight(source, mapped, false);
        foreach (var reveal in new int?[] { null, source.Text.IndexOf("中文", StringComparison.Ordinal) })
        {
            var projection = MarkdownRichTextProjection.Create(source, reveal).Text;
            var display = projection.ToDisplayRange(mapped[0].Source);
            Assert.AreEqual("\"中文😀\"", projection.Display.GetText(display));
            Assert.AreEqual(MarkdownCodeTokenKind.Comment,
                MarkdownCodeHighlighter.KindAt(highlight, projection, display.Start));
        }
    }

    [TestMethod]
    public void Another_snapshot_cannot_reuse_colors_even_with_the_same_version()
    {
        var source = new SourceTextSnapshot("```cs\nint x;\n```", 4);
        var other = MarkdownRichTextProjection.Create(new(source.Text, 4));
        Assert.IsNull(MarkdownCodeHighlighter.KindAt(new(source,
            [new(new(source.Text.IndexOf("int", StringComparison.Ordinal), 3), MarkdownCodeTokenKind.Keyword)], false),
            other.Text, other.Text.Display.Text.IndexOf("int", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Overlap_out_of_bounds_empty_and_unknown_kind_are_rejected()
    {
        var input = new MarkdownCodeInput(0, "cs", "abc", [new(new(0, 3), new(10, 3))]);
        foreach (var tokens in new ImmutableArray<MarkdownCodeToken>[]
        {
            [new(new(0, 4), MarkdownCodeTokenKind.Keyword)],
            [new(new(0, 0), MarkdownCodeTokenKind.Keyword)],
            [new(new(0, 1), (MarkdownCodeTokenKind)999)],
            [new(new(0, 2), MarkdownCodeTokenKind.Keyword), new(new(1, 1), MarkdownCodeTokenKind.String)]
        }) Assert.Throws<ArgumentException>(() => MarkdownCodeHighlighter.MapTokens(input, tokens));
    }

    [TestMethod]
    public void Empty_unclosed_block_and_fence_text_are_not_tokenized()
    {
        var source = new SourceTextSnapshot("~~~cs\n", 0);
        var inputs = MarkdownCodeHighlighter.CreateInputs(MarkdownRichTextProjection.Create(source).Text.CodeBlocks);
        Assert.HasCount(1, inputs.Blocks);
        Assert.IsFalse(inputs.Blocks[0].Text.Contains("~~~", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Number_of_code_blocks_is_bounded_without_modifying_source()
    {
        var text = string.Concat(Enumerable.Repeat("```js\nx\n```\n\n", 129));
        var source = new SourceTextSnapshot(text, 1);
        var inputs = MarkdownCodeHighlighter.CreateInputs(MarkdownRichTextProjection.Create(source).Text.CodeBlocks);
        Assert.HasCount(128, inputs.Blocks);
        Assert.IsTrue(inputs.Truncated);
        Assert.AreEqual(text, source.Text);
    }
}

[TestClass]
public sealed class StarryNightIntegrationTests
{
    private static StarryNightClient _client = null!;

    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var payload = Path.Combine(directory.FullName, "MDEditor", "Assets", "StarryNight");
            if (!File.Exists(Path.Combine(payload, "worker.mjs"))) continue;
            _client = new(payload); return;
        }
        Assert.Fail("The offline starry-night payload was not found.");
    }
    [ClassCleanup]
    public static void Cleanup() => _client?.Dispose();

    private static Task<MarkdownCodeHighlight> Run(string code, string language = "cs", long version = 1,
        CancellationToken cancellationToken = default)
    {
        var rich = MarkdownRichTextProjection.Create(new($"```{language}\n{code}\n```", version));
        return _client.HighlightAsync(MarkdownCodeHighlighter.CreateInputs(rich.Text.CodeBlocks), cancellationToken);
    }
    private static bool Has(MarkdownCodeHighlight result, string text, MarkdownCodeTokenKind kind) =>
        result.Tokens.Any(token => token.Kind == kind && result.Source.GetText(token.Source).Contains(text, StringComparison.Ordinal));

    [TestMethod]
    public async Task Csharp_has_real_entities_keywords_strings_constants_and_comments()
    {
        var result = await Run("// 中文\nint answer = 42;\nConsole.WriteLine(\"hello😀\");");
        Assert.IsTrue(Has(result, "// 中文", MarkdownCodeTokenKind.Comment));
        Assert.IsTrue(Has(result, "int", MarkdownCodeTokenKind.Keyword));
        Assert.IsTrue(Has(result, "42", MarkdownCodeTokenKind.Constant));
        Assert.IsTrue(Has(result, "WriteLine", MarkdownCodeTokenKind.Entity));
        Assert.IsTrue(Has(result, "hello😀", MarkdownCodeTokenKind.String));
    }

    [TestMethod]
    public async Task Python_multiline_strings_and_blank_lines_keep_their_state()
    {
        var result = await Run("value = \"\"\"first\n\n中文 second\"\"\"\nfor i in range(3):\n    print(i)", "python");
        Assert.IsTrue(Has(result, "中文 second", MarkdownCodeTokenKind.String));
        Assert.IsTrue(Has(result, "for", MarkdownCodeTokenKind.Keyword));
    }

    [TestMethod]
    public async Task Html_embedded_javascript_and_css_use_their_grammars()
    {
        var result = await Run("<script>const answer = 42;</script>\n<style>.box { color: red; }</style>", "html");
        Assert.IsTrue(Has(result, "script", MarkdownCodeTokenKind.Tag));
        Assert.IsTrue(Has(result, "const", MarkdownCodeTokenKind.Keyword));
        Assert.IsTrue(Has(result, "42", MarkdownCodeTokenKind.Constant));
        Assert.IsTrue(result.Tokens.Length > 8);
    }

    [TestMethod]
    public async Task Rust_beyond_the_previous_scanner_is_supported()
    {
        var result = await Run("fn main() { let value: u32 = 42; println!(\"hi\"); }", "rust");
        Assert.IsTrue(Has(result, "fn", MarkdownCodeTokenKind.Keyword));
        Assert.IsTrue(Has(result, "main", MarkdownCodeTokenKind.Entity));
    }

    [TestMethod]
    public async Task Unknown_and_language_free_code_remain_plain()
    {
        Assert.IsEmpty((await Run("int x = 42;", "a-language-that-does-not-exist")).Tokens);
        Assert.IsEmpty((await Run("int x = 42;", "")).Tokens);
    }

    [TestMethod]
    public async Task Cached_code_rebases_on_a_different_source_version()
    {
        var first = await Run("int x = 42;", version: 21);
        var second = await Run("int x = 42;", version: 22);
        Assert.AreEqual(22L, second.Source.Version);
        Assert.IsFalse(ReferenceEquals(first.Source, second.Source));
        CollectionAssert.AreEqual(first.Tokens.ToArray(), second.Tokens.ToArray());
    }

    [TestMethod]
    public async Task Cancellation_does_not_poison_the_next_request()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Run("let x = 1;", "js", cancellationToken: cancelled.Token));
        Assert.IsTrue(Has(await Run("let x = 2;", "js"), "let", MarkdownCodeTokenKind.Keyword));
    }

    [TestMethod]
    public async Task In_flight_cancellation_drains_reply_without_restarting_the_engine()
    {
        // A unique uncached block forces an actual IPC request, not a cache-only cancellation.
        var rich = MarkdownRichTextProjection.Create(new("```js\nconst cancelled = 12345;\n```", 71));
        using var cancellation = new CancellationTokenSource();
        var request = _client.HighlightAsync(MarkdownCodeHighlighter.CreateInputs(rich.Text.CodeBlocks), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => request);
        var process = _client.WorkerProcessId;
        Assert.IsTrue(Has(await Run("const kept = 67890;", "js"), "const", MarkdownCodeTokenKind.Keyword));
        if (process is not null) Assert.AreEqual(process, _client.WorkerProcessId);
    }

    [TestMethod]
    public async Task All_128_blocks_are_colored_even_when_the_LRU_retains_only_64()
    {
        var code = string.Concat(Enumerable.Range(0, 128).Select(index => $"```cs\nint value{index} = {index};\n```\n\n"));
        var rich = MarkdownRichTextProjection.Create(new(code, 72));
        var result = await _client.HighlightAsync(MarkdownCodeHighlighter.CreateInputs(rich.Text.CodeBlocks));
        Assert.IsFalse(result.Truncated);
        Assert.AreEqual(128, result.Tokens.Count(token => token.Kind == MarkdownCodeTokenKind.Keyword &&
            result.Source.GetText(token.Source) == "int"));
    }

    [TestMethod]
    public async Task Pathological_line_degrades_only_colors_not_editable_text()
    {
        var code = new string('a', 16385);
        var result = await Run(code, "js");
        Assert.IsTrue(result.Truncated);
        Assert.IsEmpty(result.Tokens);
        Assert.IsTrue(result.Source.Text.Contains(code, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Missing_packaged_runtime_fails_explicitly_without_system_node_fallback()
    {
        using var client = new StarryNightClient(Path.Combine(AppContext.BaseDirectory, "missing-starry-night"));
        var rich = MarkdownRichTextProjection.Create(new("```js\nlet x = 1;\n```", 0));
        await Assert.ThrowsAsync<FileNotFoundException>(() => client.HighlightAsync(
            MarkdownCodeHighlighter.CreateInputs(rich.Text.CodeBlocks)));
    }
}
