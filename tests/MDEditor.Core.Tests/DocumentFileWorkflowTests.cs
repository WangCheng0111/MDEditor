using System.Text;
using MDEditor.Core.Documents;
using MDEditor.Core.Text;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class DocumentFileWorkflowTests
{
    [TestMethod]
    public void Codecs_preserve_original_text_line_endings_and_bom()
    {
        const string text = "中文 $\\sqrt{x}$\r\nEnglish\n末尾\r";
        foreach (var format in Enum.GetValues<DocumentEncoding>())
        {
            var bytes = DocumentFileStore.Encode(text, format);
            var reopened = DocumentFileStore.Decode(bytes);
            Assert.AreEqual(text, reopened.Text);
            Assert.AreEqual(format, reopened.Encoding);
            CollectionAssert.AreEqual(bytes, DocumentFileStore.Encode(reopened.Text, reopened.Encoding));
        }
    }

    [TestMethod]
    public void Invalid_encoding_or_unsupported_controls_never_create_a_document()
    {
        Assert.ThrowsExactly<DecoderFallbackException>(() => DocumentFileStore.Decode([0xC3, 0x28]));
        Assert.ThrowsExactly<DecoderFallbackException>(() => DocumentFileStore.Decode([0xFF, 0xFE, 0x00]));
        Assert.ThrowsExactly<InvalidDataException>(() => DocumentFileStore.Decode(Encoding.UTF8.GetBytes("a\tb")));
        Assert.ThrowsExactly<InvalidDataException>(() => DocumentFileStore.Decode(Encoding.UTF8.GetBytes("a\0b")));
    }

    [TestMethod]
    public async Task Save_is_atomic_and_detects_external_replacement()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, "draft.md");
            var files = new DocumentFileStore();
            var firstHash = await files.SaveAsync(path, "原文\r\n", DocumentEncoding.Utf8Bom, null);
            var first = await files.OpenAsync(path);
            Assert.AreEqual(firstHash, first.Fingerprint);
            Assert.AreEqual(DocumentEncoding.Utf8Bom, first.Encoding);
            var secondHash = await files.SaveAsync(path, "新文\r\n", first.Encoding, first.Fingerprint);
            Assert.AreNotEqual(firstHash, secondHash);
            await File.WriteAllTextAsync(path, "外部版本");
            await Assert.ThrowsExactlyAsync<DocumentChangedOnDiskException>(() =>
                files.SaveAsync(path, "编辑器版本", first.Encoding, secondHash));
            Assert.AreEqual("外部版本", await File.ReadAllTextAsync(path));
            Assert.AreEqual(0, Directory.EnumerateFiles(directory, "*.tmp").Count());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Recovery_is_checksummed_round_trips_and_clears()
    {
        var directory = TempDirectory();
        try
        {
            var store = new DocumentRecoveryStore(directory);
            Assert.IsNull(await store.ReadAsync());
            var checkpoint = new RecoveryCheckpoint("甲\r\n$y+1$", [new(0, 4)],
                "C:\\example\\note.md", DocumentEncoding.Utf16Le, "ABCD", DateTimeOffset.UtcNow);
            await store.WriteAsync(checkpoint);
            var restored = await new DocumentRecoveryStore(directory).ReadAsync();
            Assert.IsNotNull(restored);
            Assert.AreEqual(checkpoint.Text, restored.Text);
            Assert.AreEqual(checkpoint.FilePath, restored.FilePath);
            Assert.AreEqual(checkpoint.Encoding, restored.Encoding);
            CollectionAssert.AreEqual(checkpoint.Styles, restored.Styles);
            var recoveryFile = Path.Combine(directory, "active-document.recovery.json");
            var content = await File.ReadAllTextAsync(recoveryFile);
            Assert.IsTrue(content.Contains("ABCD"));
            await File.WriteAllTextAsync(recoveryFile, content.Replace("ABCD", "WXYZ"));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.ReadAsync());
            await store.ClearAsync();
            Assert.IsNull(await store.ReadAsync());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void Session_tracks_undo_back_to_saved_text_and_edit_during_save()
    {
        var session = new DocumentSessionState();
        session.Load("saved", "C:\\note.md", DocumentEncoding.Utf8, "OLD");
        Assert.IsFalse(session.IsDirty);
        session.Edited("changed");
        Assert.IsTrue(session.IsDirty);
        session.Edited("saved");
        Assert.IsFalse(session.IsDirty);
        session.Edited("during save");
        var epoch = session.Epoch;
        Assert.IsTrue(session.CompleteSave(epoch, "C:\\note.md", DocumentEncoding.Utf8,
            "saved", "NEW", "during save"));
        Assert.IsTrue(session.IsDirty);
        session.Edited("saved");
        Assert.IsFalse(session.IsDirty);
        session.Load("different", null, DocumentEncoding.Utf8, null);
        Assert.IsFalse(session.CompleteSave(epoch, "C:\\old.md", DocumentEncoding.Utf8,
            "saved", "STALE", "different"));
        Assert.IsNull(session.FilePath);
        session.Load("recovered", "C:\\note.md", DocumentEncoding.Utf8Bom, "DISK", recovered: true);
        Assert.IsTrue(session.IsDirty);
    }

    [TestMethod]
    public void Newline_insertion_uses_current_line_or_prior_fallback()
    {
        var lines = new DocumentTextBuffer("a\r\nb\nc\r").CaptureLineMap();
        Assert.AreEqual("\r\n", DocumentNewlinePolicy.ForInsertion(lines, 0));
        Assert.AreEqual("\n", DocumentNewlinePolicy.ForInsertion(lines, 3));
        Assert.AreEqual("\r", DocumentNewlinePolicy.ForInsertion(lines, 6));
        Assert.AreEqual("\r", DocumentNewlinePolicy.ForInsertion(lines, 7));
        Assert.AreEqual("\n", DocumentNewlinePolicy.ForInsertion(new DocumentTextBuffer("plain").CaptureLineMap(), 2));
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "MDEditor-Step31-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
