using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class DocumentSearchTests
{
    [TestMethod]
    public void Reopening_find_adopts_the_new_selection_after_an_existing_successful_search()
    {
        var source = new SourceTextSnapshot("原生排版，原生。", 3);
        var previous = DocumentSearch.Find(source, "原生");
        Assert.HasCount(2, previous.Matches);
        var query = DocumentSearch.QueryFromSelection(source, new(2, 2), previous.Query, false);
        Assert.AreEqual("排版", query);
        Assert.AreEqual(new SourceRange(2, 2), DocumentSearch.Find(source, query).Matches.Single());
        Assert.AreEqual("原生排版，原生。", source.Text);
    }

    [TestMethod]
    public void Ctrl_f_inside_find_or_replace_does_not_reuse_the_previous_document_selection()
    {
        var source = new SourceTextSnapshot("原生排版", 0);
        Assert.AreEqual("新搜索词", DocumentSearch.QueryFromSelection(source, new(0, 2), "新搜索词", true));
        Assert.AreEqual("", DocumentSearch.QueryFromSelection(source, new(0, 2), "", true));
    }

    [TestMethod]
    public void Without_a_document_selection_the_current_query_is_retained()
    {
        var source = new SourceTextSnapshot("原生排版", 0);
        Assert.AreEqual("原生", DocumentSearch.QueryFromSelection(source, null, "原生", false));
        Assert.AreEqual("原生", DocumentSearch.QueryFromSelection(source, new(2, 0), "原生", false));
    }

    [TestMethod]
    public void Selection_seeding_preserves_the_existing_short_query_limit()
    {
        var source = new SourceTextSnapshot(new string('a', 512), 0);
        Assert.AreEqual(new string('a', 511),
            DocumentSearch.QueryFromSelection(source, new(0, 511), "原生", false));
        Assert.AreEqual("原生", DocumentSearch.QueryFromSelection(source, new(0, 512), "原生", false));
    }

    [TestMethod]
    [DataRow("a\nb")]
    [DataRow("a\rb")]
    [DataRow("a\r\nb")]
    public void Multiline_selection_does_not_replace_the_search_input(string text)
    {
        var source = new SourceTextSnapshot(text, 0);
        Assert.AreEqual("原生", DocumentSearch.QueryFromSelection(source, source.FullRange, "原生", false));
    }

    [TestMethod]
    public void Seeding_keeps_selected_unicode_and_literal_whitespace_unchanged()
    {
        var source = new SourceTextSnapshot(" 中文😀cafe\u0301 ", 6);
        Assert.AreEqual(source.Text,
            DocumentSearch.QueryFromSelection(source, source.FullRange, "原生", false));
        Assert.AreEqual(" ", DocumentSearch.QueryFromSelection(source, new(0, 1), "原生", false));
    }

    [TestMethod]
    [DataRow("**", 2)]
    [DataRow("\\sqrt", 1)]
    [DataRow("https://example.com", 1)]
    [DataRow("[^n]", 2)]
    [DataRow("|", 9)]
    public void Hidden_markers_addresses_tables_and_math_use_original_ranges(string query, int count)
    {
        var source = new SourceTextSnapshot("**加粗** [链接](https://example.com) $\\frac{x}{\\sqrt{y}}$ [^n]\r\n\r\n" +
            "| a | b |\r\n|---|---|\r\n| 1 | 2 |\r\n\r\n[^n]: footnote", 15);
        var result = DocumentSearch.Find(source, query);
        Assert.AreSame(source, result.Source);
        Assert.AreEqual(count, result.Matches.Length);
        foreach (var match in result.Matches) Assert.AreEqual(query, source.GetText(match));
    }

    [TestMethod]
    public void Literal_case_whole_word_and_unicode_boundaries()
    {
        var source = new SourceTextSnapshot("FOR for before for_ for2 (for) 中文 中 café caféx 😀x", 1);
        Assert.HasCount(6, DocumentSearch.Find(source, "for").Matches);
        Assert.HasCount(5, DocumentSearch.Find(source, "for", new(MatchCase: true)).Matches);
        Assert.HasCount(3, DocumentSearch.Find(source, "for", new(WholeWord: true)).Matches);
        Assert.HasCount(2, DocumentSearch.Find(source, "for", new(true, true)).Matches);
        Assert.HasCount(1, DocumentSearch.Find(source, "中", new(WholeWord: true)).Matches);
        Assert.HasCount(1, DocumentSearch.Find(source, "café", new(WholeWord: true)).Matches);
    }

    [TestMethod]
    public void Multiline_literal_keeps_crlf_and_does_not_normalize_accents()
    {
        var source = new SourceTextSnapshot("a\r\nb café cafe\u0301", 1);
        Assert.AreEqual(new SourceRange(0, 4), DocumentSearch.Find(source, "a\r\nb").Matches.Single());
        Assert.HasCount(1, DocumentSearch.Find(source, "café").Matches);
        Assert.HasCount(1, DocumentSearch.Find(source, "cafe\u0301").Matches);
    }

    [TestMethod]
    public void Nonoverlapping_literal_navigation_wraps_both_ways()
    {
        var result = DocumentSearch.Find(new("aaaa", 0), "aa");
        CollectionAssert.AreEqual(new[] { new SourceRange(0, 2), new SourceRange(2, 2) }, result.Matches.ToArray());
        Assert.AreEqual(0, result.FindIndex(0));
        Assert.AreEqual(1, result.FindIndex(1));
        Assert.AreEqual(0, result.FindIndex(4));
        Assert.AreEqual(1, result.FindIndex(0, true));
        Assert.AreEqual(0, result.FindIndex(2, true));
    }

    [TestMethod]
    public void Empty_query_and_invalid_regex_are_safe()
    {
        var source = new SourceTextSnapshot("abc", 0);
        Assert.IsEmpty(DocumentSearch.Find(source, "").Matches);
        Assert.AreEqual(-1, DocumentSearch.Find(source, "z").FindIndex(0));
        Assert.IsNotNull(DocumentSearch.Find(source, "[", new(RegularExpression: true)).Error);
        Assert.IsNotNull(DocumentSearch.Find(source, new string('a', 4097)).Error);
    }

    [TestMethod]
    public void Regex_zero_length_matches_progress_and_support_capture_replacement()
    {
        var buffer = new StyledDocumentBuffer("abc\r\ndef", [new(0, 0)], 0);
        var before = buffer.Capture();
        var zero = DocumentSearch.Find(before.Source, "(?=.)", new(RegularExpression: true));
        Assert.HasCount(7, zero.Matches); // Includes CR; dot excludes LF.
        var result = DocumentSearch.Find(before.Source, "(abc)|(def)", new(RegularExpression: true));
        var plan = DocumentSearch.PlanReplace(before, result, "[$&]")!;
        Assert.AreEqual("[abc]\r\n[def]", plan.Text);
        Assert.AreEqual(2, plan.Count);
    }

    [TestMethod]
    public void Replacement_can_delete_insert_or_select_one_match_and_does_not_rescan_new_text()
    {
        var buffer = new StyledDocumentBuffer("aa aa", [new(0, 0)], 0);
        var before = buffer.Capture();
        var result = DocumentSearch.Find(before.Source, "aa");
        Assert.AreEqual(" ", DocumentSearch.PlanReplace(before, result, "")!.Text);
        Assert.AreEqual("aaa aaa", DocumentSearch.PlanReplace(before, result, "aaa")!.Text);
        Assert.AreEqual(new SourceRange(3, 2), DocumentSearch.PlanReplace(before, result, "b", 1)!.Range);
        Assert.IsNull(DocumentSearch.PlanReplace(before, result, "aa"));
        var zero = DocumentSearch.Find(before.Source, "^|$", new(RegularExpression: true));
        Assert.AreEqual("!aa aa!", DocumentSearch.PlanReplace(before, zero, "!")!.Text);
    }

    [TestMethod]
    public void All_replace_preserves_unaffected_styles_and_is_one_undo_redo_group()
    {
        var buffer = new StyledDocumentBuffer("a xx\r\nb xx\r\nc", [new(0, 0), new(6, 1), new(12, 2)], 10);
        var before = buffer.Capture();
        var anchor = new TextCaret(TextSurface.Body, before.Source.Version, 2, CaretAffinity.Downstream);
        var selection = new TextSelection(anchor, anchor);
        var history = new TextEditHistory(buffer);
        var plan = DocumentSearch.PlanReplace(before, DocumentSearch.Find(before.Source, "xx"), "long")!;
        var change = buffer.Restore(plan.Range, before.Source.GetText(plan.Range), plan.Text, plan.Styles, before.Source.Version);
        var caret = anchor with { SourceVersion = change.NewVersion, Offset = plan.CaretOffset };
        history.Record(before, selection, new(change, new(caret, caret)), HistoryEditKind.Replace, DateTimeOffset.UtcNow);
        Assert.AreEqual("a long\r\nb long\r\nc", buffer.Capture().Source.Text);
        CollectionAssert.AreEqual(new[] { new DocumentStyleMarker(0, 0), new(8, 1), new(16, 2) }, buffer.Capture().Styles.ToArray());
        Assert.AreEqual(2, history.Undo()!.Value.Focus.Offset);
        Assert.AreEqual(before.Source.Text, buffer.Capture().Source.Text);
        CollectionAssert.AreEqual(before.Styles.ToArray(), buffer.Capture().Styles.ToArray());
        Assert.IsFalse(history.CanUndo);
        Assert.IsNotNull(history.Redo());
        Assert.AreEqual("a long\r\nb long\r\nc", buffer.Capture().Source.Text);
    }

    [TestMethod]
    public void Boundaries_inside_deleted_matches_merge_but_between_matches_remain()
    {
        var buffer = new StyledDocumentBuffer("ABx ABx", [new(0, 0), new(1, 1), new(4, 2), new(5, 3)], 0);
        var snapshot = buffer.Capture();
        var plan = DocumentSearch.PlanReplace(snapshot, DocumentSearch.Find(snapshot.Source, "AB"), "Z")!;
        CollectionAssert.AreEqual(new[] { new DocumentStyleMarker(0, 0), new(3, 2) }, plan.Styles.ToArray());
    }

    [TestMethod]
    public void Stale_results_and_invalid_index_never_mutate_document()
    {
        var buffer = new StyledDocumentBuffer("abc", [new(0, 0)], 0);
        var result = DocumentSearch.Find(buffer.Capture().Source, "a");
        buffer.Replace(new(3, 0), "z");
        Assert.ThrowsExactly<InvalidOperationException>(() => DocumentSearch.PlanReplace(buffer.Capture(), result, "X"));
        var current = DocumentSearch.Find(buffer.Capture().Source, "a");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DocumentSearch.PlanReplace(buffer.Capture(), current, "X", 10));
        Assert.AreEqual("abcz", buffer.Capture().Source.Text);
    }

    [TestMethod]
    public void Match_budget_does_not_allow_silent_partial_replace_all()
    {
        var buffer = new StyledDocumentBuffer(new string('a', DocumentSearch.MaximumMatches + 1), [new(0, 0)], 0);
        var result = DocumentSearch.Find(buffer.Capture().Source, "a");
        Assert.IsTrue(result.Truncated);
        Assert.AreEqual(DocumentSearch.MaximumMatches, result.Matches.Length);
        Assert.ThrowsExactly<InvalidOperationException>(() => DocumentSearch.PlanReplace(buffer.Capture(), result, "b"));
        Assert.IsNotNull(DocumentSearch.PlanReplace(buffer.Capture(), result, "b", 0));
    }

    [TestMethod]
    public void Cancellation_and_regex_timeout_are_bounded()
    {
        var source = new SourceTextSnapshot(new string('a', 10_000) + "!", 0);
        Assert.ThrowsExactly<OperationCanceledException>(() => DocumentSearch.Find(source, "a", cancellationToken: new(true)));
        var result = DocumentSearch.Find(source, "(a+)+$", new(RegularExpression: true));
        Assert.IsNotNull(result.Error);
        Assert.IsEmpty(result.Matches);
    }

    [TestMethod]
    public void Partial_surrogate_matches_are_excluded()
    {
        var source = new SourceTextSnapshot("😀😀", 0);
        Assert.IsEmpty(DocumentSearch.Find(source, "\ud83d").Matches);
        Assert.HasCount(2, DocumentSearch.Find(source, "😀").Matches);
    }
}
