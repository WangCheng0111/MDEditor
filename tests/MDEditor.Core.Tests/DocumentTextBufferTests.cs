using MDEditor.Core.Text;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class DocumentTextBufferTests
{
    [TestMethod]
    public void Empty_document_has_one_empty_line_and_stable_snapshot()
    {
        var buffer = new DocumentTextBuffer();
        var first = buffer.CaptureSnapshot();
        Assert.AreSame(first, buffer.CaptureSnapshot());
        Assert.AreEqual(0L, first.Version);
        Assert.AreEqual("", first.Text);
        var lines = buffer.CaptureLineMap();
        Assert.AreSame(lines, buffer.CaptureLineMap());
        Assert.AreEqual(1, lines.Count);
        Assert.AreEqual(new SourceRange(0, 0), lines.Lines[0].FullRange);
        Assert.AreEqual(0, lines.FindLine(0));
    }

    [TestMethod]
    public void Mixed_line_endings_are_preserved_byte_for_byte_as_utf16_text()
    {
        var buffer = new DocumentTextBuffer("A\r\nB\nC\rD\r\n", 12);
        var map = buffer.CaptureLineMap();
        CollectionAssert.AreEqual(new[] { DocumentLineEnding.CrLf, DocumentLineEnding.Lf,
            DocumentLineEnding.Cr, DocumentLineEnding.CrLf, DocumentLineEnding.None },
            map.Lines.Select(line => line.Ending).ToArray());
        CollectionAssert.AreEqual(new[] { 2, 1, 1, 2, 0 },
            map.Lines.Select(line => line.Break.Length).ToArray());
        Assert.AreEqual("A\r\nB\nC\rD\r\n", buffer.CaptureSnapshot().Text);
        Assert.AreEqual(0, map.FindLine(2)); // The LF belongs to the preceding CRLF line.
        Assert.AreEqual(1, map.FindLine(3));
        Assert.AreEqual(4, map.FindLine(buffer.Length));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => map.FindLine(buffer.Length + 1));
    }

    [TestMethod]
    public void Cross_line_replacement_keeps_old_snapshot_and_exact_new_breaks()
    {
        var buffer = new DocumentTextBuffer("甲\r\n乙\n丙", 7);
        var old = buffer.CaptureSnapshot();
        var oldLines = buffer.CaptureLineMap();
        var edit = buffer.Replace(new(3, 3), "🌍\r丁");
        Assert.IsTrue(edit.Changed);
        Assert.AreEqual(7L, edit.OldVersion);
        Assert.AreEqual(8L, edit.NewVersion);
        Assert.AreEqual(new SourceRange(3, 3), edit.Changes.Single().OldRange);
        Assert.AreEqual(new SourceRange(3, 4), edit.Changes.Single().NewRange);
        Assert.AreEqual("乙\n丙", edit.Changes.Single().OldText);
        Assert.AreEqual("🌍\r丁", edit.Changes.Single().NewText);
        Assert.AreEqual("甲\r\n乙\n丙", old.Text);
        Assert.AreEqual("甲\r\n🌍\r丁", buffer.CaptureSnapshot().Text);
        Assert.AreEqual(3, oldLines.Count);
        CollectionAssert.AreEqual(new[] { DocumentLineEnding.CrLf, DocumentLineEnding.Cr, DocumentLineEnding.None },
            buffer.CaptureLineMap().Lines.Select(line => line.Ending).ToArray());
    }

    [TestMethod]
    public void Multi_edit_transaction_uses_original_coordinates_and_maps_new_ranges()
    {
        var buffer = new DocumentTextBuffer("ab\r\ncd\nEF", 3);
        using var transaction = buffer.BeginTransaction();
        transaction.Replace(new(7, 2), "世界");
        transaction.Insert(4, "😀");
        transaction.Replace(new(0, 2), "A");
        var result = transaction.Commit();
        Assert.AreEqual("A\r\n😀cd\n世界", buffer.CaptureSnapshot().Text);
        Assert.AreEqual(4L, buffer.Version);
        CollectionAssert.AreEqual(new[] { new SourceRange(0, 2), new SourceRange(4, 0), new SourceRange(7, 2) },
            result.Changes.Select(change => change.OldRange).ToArray());
        CollectionAssert.AreEqual(new[] { new SourceRange(0, 1), new SourceRange(3, 2), new SourceRange(8, 2) },
            result.Changes.Select(change => change.NewRange).ToArray());
        Assert.ThrowsExactly<InvalidOperationException>(() => transaction.Insert(0, "x"));
    }

    [TestMethod]
    public void Overlap_or_duplicate_insertion_rejects_whole_batch_without_mutation()
    {
        var buffer = new DocumentTextBuffer("abcdef");
        using (var transaction = buffer.BeginTransaction())
        {
            transaction.Delete(new(1, 3));
            transaction.Replace(new(2, 1), "Z");
            Assert.ThrowsExactly<ArgumentException>(() => transaction.Commit());
        }
        Assert.AreEqual("abcdef", buffer.CaptureSnapshot().Text);
        Assert.AreEqual(0L, buffer.Version);
        using (var transaction = buffer.BeginTransaction())
        {
            transaction.Insert(2, "A");
            transaction.Insert(2, "B");
            Assert.ThrowsExactly<ArgumentException>(() => transaction.Commit());
        }
        Assert.AreEqual("abcdef", buffer.CaptureSnapshot().Text);
    }

    [TestMethod]
    public void Stale_transaction_cannot_overwrite_a_newer_commit()
    {
        var buffer = new DocumentTextBuffer("abc", 9);
        using var first = buffer.BeginTransaction();
        using var stale = buffer.BeginTransaction();
        first.Insert(3, "d");
        stale.Delete(new(0, 1));
        first.Commit();
        var error = Assert.ThrowsExactly<DocumentVersionConflictException>(() => stale.Commit());
        Assert.AreEqual(9L, error.ExpectedVersion);
        Assert.AreEqual(10L, error.ActualVersion);
        Assert.AreEqual("abcd", buffer.CaptureSnapshot().Text);
    }

    [TestMethod]
    public void Identical_or_empty_changes_do_not_advance_the_version()
    {
        var buffer = new DocumentTextBuffer("abc", 7);
        var snapshot = buffer.CaptureSnapshot();
        var identical = buffer.Replace(new(0, 3), "abc");
        var empty = buffer.Insert(1, "");
        Assert.IsFalse(identical.Changed);
        Assert.IsFalse(empty.Changed);
        Assert.AreEqual(7L, buffer.Version);
        Assert.AreSame(snapshot, buffer.CaptureSnapshot());
    }

    [TestMethod]
    public void Supplementary_unicode_and_combining_marks_survive_edits()
    {
        var buffer = new DocumentTextBuffer("a😀e\u0301中");
        buffer.Insert(3, "👩‍💻");
        Assert.AreEqual("a😀👩‍💻e\u0301中", buffer.CaptureSnapshot().Text);
        var emojiLength = "👩‍💻".Length;
        buffer.Delete(new(3, emojiLength));
        Assert.AreEqual("a😀e\u0301中", buffer.CaptureSnapshot().Text);
        Assert.ThrowsExactly<ArgumentException>(() => buffer.Insert(2, "x"));
        Assert.ThrowsExactly<ArgumentException>(() => buffer.Delete(new(1, 1)));
        Assert.ThrowsExactly<ArgumentException>(() => buffer.Insert(0, "\uD800"));
        Assert.ThrowsExactly<ArgumentException>(() => new DocumentTextBuffer("\uDC00"));
        Assert.AreEqual("a😀e\u0301中", buffer.CaptureSnapshot().Text);
    }

    [TestMethod]
    public void CrLf_is_one_edit_boundary_but_cr_and_lf_remain_distinct_in_the_source()
    {
        var buffer = new DocumentTextBuffer("a\r\nb");
        Assert.ThrowsExactly<ArgumentException>(() => buffer.Insert(2, "x"));
        Assert.ThrowsExactly<ArgumentException>(() => buffer.Delete(new(1, 1)));
        buffer.Delete(new(1, 2));
        Assert.AreEqual("ab", buffer.CaptureSnapshot().Text);
        Assert.AreEqual(1, buffer.CaptureLineMap().Count);
        buffer.Insert(1, "\r\n");
        Assert.AreEqual("a\r\nb", buffer.CaptureSnapshot().Text);
        Assert.AreEqual(DocumentLineEnding.CrLf, buffer.CaptureLineMap().Lines[0].Ending);
    }

    [TestMethod]
    public void CrLf_formed_across_pieces_is_indexed_and_protected_as_one_break()
    {
        var buffer = new DocumentTextBuffer("a\rX\nb");
        buffer.Delete(new(2, 1));
        Assert.AreEqual("a\r\nb", buffer.CaptureSnapshot().Text);
        Assert.AreEqual(DocumentLineEnding.CrLf, buffer.CaptureLineMap().Lines[0].Ending);
        Assert.ThrowsExactly<ArgumentException>(() => buffer.Insert(2, "z"));
    }

    [TestMethod]
    public void Cancelled_transaction_and_invalid_range_leave_document_unchanged()
    {
        var buffer = new DocumentTextBuffer("text");
        using (var transaction = buffer.BeginTransaction())
        {
            transaction.Insert(1, "x");
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => transaction.Delete(new(5, 1)));
        }
        Assert.AreEqual("text", buffer.CaptureSnapshot().Text);
        Assert.AreEqual(0L, buffer.Version);
    }

    [TestMethod]
    public void Version_overflow_rejects_a_real_edit_but_allows_a_no_op()
    {
        var buffer = new DocumentTextBuffer("x", long.MaxValue);
        Assert.IsFalse(buffer.Replace(new(0, 1), "x").Changed);
        Assert.ThrowsExactly<InvalidOperationException>(() => buffer.Insert(1, "y"));
        Assert.AreEqual("x", buffer.CaptureSnapshot().Text);
        Assert.AreEqual(long.MaxValue, buffer.Version);
    }

    [TestMethod]
    public void Piece_table_matches_string_oracle_over_many_unicode_and_newline_edits()
    {
        var random = new Random(15092026);
        var buffer = new DocumentTextBuffer("one\r\ntwo😀\n三");
        var expected = buffer.CaptureSnapshot().Text;
        var fragments = new[] { "", "A", "中", "😀", "e\u0301", "\r\n", "\n", "\r", "-" };
        for (var step = 0; step < 700; step++)
        {
            var boundaries = Enumerable.Range(0, expected.Length + 1).Where(position =>
                position == 0 || position == expected.Length ||
                !(char.IsHighSurrogate(expected[position - 1]) && char.IsLowSurrogate(expected[position]) ||
                  expected[position - 1] == '\r' && expected[position] == '\n')).ToArray();
            var firstIndex = random.Next(boundaries.Length);
            var secondIndex = random.Next(firstIndex, boundaries.Length);
            var start = boundaries[firstIndex];
            var end = boundaries[secondIndex];
            var insert = fragments[random.Next(fragments.Length)];
            var replacement = expected.Remove(start, end - start).Insert(start, insert);
            var oldVersion = buffer.Version;
            var result = buffer.Replace(new(start, end - start), insert);
            expected = replacement;
            Assert.AreEqual(expected, buffer.CaptureSnapshot().Text, $"Mismatch after edit {step}.");
            Assert.AreEqual(expected.Length, buffer.Length);
            Assert.AreEqual(oldVersion + (result.Changed ? 1 : 0), buffer.Version);
        }
    }

    [TestMethod]
    public void Batched_edits_match_descending_string_oracle()
    {
        var random = new Random(15092027);
        var buffer = new DocumentTextBuffer("a😀b\r\nc中d\ne");
        var fragments = new[] { "", "Q", "文", "😀", "\r\n", "e\u0301" };
        for (var step = 0; step < 300; step++)
        {
            var original = buffer.CaptureSnapshot().Text;
            var boundaries = Enumerable.Range(0, original.Length + 1).Where(position =>
                position == 0 || position == original.Length ||
                !(char.IsHighSurrogate(original[position - 1]) && char.IsLowSurrogate(original[position]) ||
                  original[position - 1] == '\r' && original[position] == '\n')).ToArray();
            var points = Enumerable.Range(0, 4).Select(_ => boundaries[random.Next(boundaries.Length)]).Order().ToArray();
            if (points[0] == points[2]) continue;
            var edits = new[]
            {
                (Range: new SourceRange(points[0], points[1] - points[0]), Text: fragments[random.Next(fragments.Length)]),
                (Range: new SourceRange(points[2], points[3] - points[2]), Text: fragments[random.Next(fragments.Length)])
            };
            if (edits[0].Range.Start == edits[1].Range.Start) continue;
            var expected = original;
            foreach (var edit in edits.Reverse())
                expected = expected.Remove(edit.Range.Start, edit.Range.Length).Insert(edit.Range.Start, edit.Text);
            using var transaction = buffer.BeginTransaction();
            transaction.Replace(edits[1].Range, edits[1].Text);
            transaction.Replace(edits[0].Range, edits[0].Text);
            var result = transaction.Commit();
            Assert.AreEqual(expected, buffer.CaptureSnapshot().Text, $"Mismatch after batch {step}.");
            Assert.AreEqual(expected.Length, result.NewLength);
            Assert.IsTrue(result.Changes.All(change =>
                expected.Substring(change.NewRange.Start, change.NewRange.Length) == change.NewText));
        }
    }
}
