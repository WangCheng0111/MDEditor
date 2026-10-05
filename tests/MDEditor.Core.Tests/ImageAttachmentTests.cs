using MDEditor.Core.Documents;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class ImageAttachmentTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3XcAAAAASUVORK5CYII=");

    [TestMethod]
    public async Task Saved_document_uses_unique_relative_png_without_overwriting_existing_files()
    {
        using var scope = new Scope();
        var store = new ImageAttachmentStore(scope.Local);
        var first = await store.StorePngAsync(Png, scope.Document);
        var second = await store.StorePngAsync(Png, scope.Document);
        Assert.AreNotEqual(first.FullPath, second.FullPath);
        Assert.IsTrue(first.Target.StartsWith("assets/mdeditor-image-", StringComparison.Ordinal));
        Assert.AreEqual(Path.GetFullPath(Path.Combine(scope.Document, first.Target)), first.FullPath);
        CollectionAssert.AreEqual(Png, await File.ReadAllBytesAsync(first.FullPath));
        var projection = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(new(first.Markdown, 0)));
        Assert.AreEqual(first.Target, projection.References.Images.Single().Target);
        Assert.AreEqual("\uFFFC", projection.Display.Text);
    }

    [TestMethod]
    public async Task Unsaved_document_uses_durable_encoded_file_uri_not_a_temp_or_package_path()
    {
        using var scope = new Scope();
        var image = await new ImageAttachmentStore(scope.Local).StorePngAsync(Png, null);
        Assert.IsTrue(image.Target.StartsWith("file:///", StringComparison.Ordinal));
        Assert.IsFalse(image.Target.Contains(' '));
        Assert.IsTrue(image.Target.Contains("%28", StringComparison.Ordinal));
        Assert.IsFalse(image.Target.Contains(')'));
        Assert.AreEqual(image.FullPath, new Uri(image.Target).LocalPath);
        Assert.AreEqual(Path.Combine(scope.Local, "ClipboardImages"), Path.GetDirectoryName(image.FullPath));
        Assert.AreEqual(image.Target, MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(
            new(image.Markdown, 0))).References.Images.Single().Target);
    }

    [TestMethod]
    public async Task First_save_copies_to_document_assets_and_changes_only_real_image_destinations()
    {
        using var scope = new Scope();
        var store = new ImageAttachmentStore(scope.Local);
        var image = await store.StorePngAsync(Png, null);
        store.Commit(image);
        var text = $"中文😀 {image.Markdown}\r\n\r\n```md\n{image.Markdown}\n```\n`{image.Markdown}`\n";
        var styles = new[] { new DocumentStyleMarker(0, 1), new(text.IndexOf("```", StringComparison.Ordinal), 2) };
        var before = new StyledDocumentBuffer(text, styles, 10).Capture();
        var plan = await store.PrepareSaveAsync(before, null, Path.Combine(scope.Document, "文档.md"));
        Assert.IsTrue(plan.Changed);
        Assert.AreEqual(1, plan.Rewrites.Count);
        var rewritten = plan.Rewrites.Single();
        Assert.AreEqual(image.Target, before.Source.GetText(rewritten.Range));
        Assert.AreEqual(text.Replace(image.Markdown + "\r\n", $"![image]({rewritten.Target})\r\n"), plan.Saved.Source.Text);
        CollectionAssert.AreEqual(Png, await File.ReadAllBytesAsync(Path.Combine(scope.Document, rewritten.Target)));
        Assert.IsTrue(File.Exists(image.FullPath)); // Recovery/undo still refer to the original durable file.
        Assert.AreEqual(styles[1].Offset + rewritten.Target.Length - rewritten.Range.Length, plan.Saved.Styles[1].Offset);
        Assert.AreEqual(before.Source.Text, text);
    }

    [TestMethod]
    public async Task Save_as_copies_generated_relative_images_but_preserves_user_managed_links()
    {
        using var scope = new Scope();
        var store = new ImageAttachmentStore(scope.Local);
        var image = await store.StorePngAsync(Png, scope.Document);
        var another = Path.Combine(scope.Root, "另一位置");
        var before = Snapshot($"{image.Markdown}\n![manual](images/user.png)\n![remote](https://example.invalid/x.png)");
        var plan = await store.PrepareSaveAsync(before, scope.Document, Path.Combine(another, "copy.md"));
        Assert.IsFalse(plan.Changed);
        Assert.AreSame(before, plan.Saved);
        Assert.IsTrue(File.Exists(Path.Combine(another, image.Target)));
        Assert.IsTrue(File.Exists(image.FullPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(another, "images")));
    }

    [TestMethod]
    public async Task Repeated_references_share_one_attachment_and_repeated_saves_reuse_it()
    {
        using var scope = new Scope();
        var store = new ImageAttachmentStore(scope.Local);
        var image = await store.StorePngAsync(Png, null);
        var before = Snapshot(image.Markdown + "\n" + image.Markdown);
        var path = Path.Combine(scope.Document, "repeat.md");
        var plan = await store.PrepareSaveAsync(before, null, path);
        Assert.AreEqual(2, plan.Rewrites.Count);
        Assert.AreEqual(plan.Rewrites[0].Target, plan.Rewrites[1].Target);
        var again = await store.PrepareSaveAsync(before, null, path);
        Assert.AreEqual(plan.Saved.Source.Text, again.Saved.Source.Text);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(scope.Document, "assets")).Length);
    }

    [TestMethod]
    public async Task Destination_collision_never_overwrites_an_existing_different_file()
    {
        using var scope = new Scope();
        var store = new ImageAttachmentStore(scope.Local);
        var image = await store.StorePngAsync(Png, null);
        var assets = Path.Combine(scope.Document, "assets");
        Directory.CreateDirectory(assets);
        var collision = Path.Combine(assets, Path.GetFileName(image.FullPath));
        await File.WriteAllTextAsync(collision, "must keep");
        var plan = await store.PrepareSaveAsync(Snapshot(image.Markdown), null, Path.Combine(scope.Document, "note.md"));
        Assert.AreNotEqual("assets/" + Path.GetFileName(image.FullPath), plan.Rewrites.Single().Target);
        Assert.AreEqual("must keep", await File.ReadAllTextAsync(collision));
        CollectionAssert.AreEqual(Png, await File.ReadAllBytesAsync(Path.Combine(scope.Document, plan.Rewrites.Single().Target)));
    }

    [TestMethod]
    public async Task Save_plan_maps_unicode_selections_and_preserves_undo_redo()
    {
        using var scope = new Scope();
        var store = new ImageAttachmentStore(scope.Local);
        var first = await store.StorePngAsync(Png, null);
        var second = await store.StorePngAsync(Png, null);
        var buffer = new StyledDocumentBuffer("甲😀" + first.Markdown + "乙" + second.Markdown + "尾", [new(0, 4)]);
        var before = buffer.Capture();
        var selection = Selection(before.Source, 1, before.Source.Length);
        var history = new TextEditHistory(buffer);
        var plan = await store.PrepareSaveAsync(before, null, Path.Combine(scope.Document, "note.md"));
        Assert.AreEqual(1, plan.MapOffset(1));
        Assert.AreEqual(plan.Saved.Source.Length, plan.MapOffset(before.Source.Length));
        foreach (var rewrite in plan.Rewrites)
        {
            Assert.IsTrue(plan.MapOffset(rewrite.Range.End) >= plan.MapOffset(rewrite.Range.Start));
            Assert.AreEqual(rewrite.Target.Length, plan.MapOffset(rewrite.Range.End) - plan.MapOffset(rewrite.Range.Start));
        }
        var change = buffer.Restore(plan.ChangedRange, before.Source.GetText(plan.ChangedRange),
            plan.ChangedText, plan.Saved.Styles, before.Source.Version);
        var next = Selection(buffer.Capture().Source, plan.MapOffset(selection.Anchor.Offset), plan.MapOffset(selection.Focus.Offset));
        history.Record(before, selection, new(change, next), HistoryEditKind.Replace, DateTimeOffset.UtcNow);
        Assert.AreEqual(plan.Saved.Source.Text, buffer.Capture().Source.Text);
        Assert.AreEqual(selection.Range, history.Undo()!.Value.Range);
        Assert.AreEqual(before.Source.Text, buffer.Capture().Source.Text);
        Assert.AreEqual(next.Range, history.Redo()!.Value.Range);
        Assert.AreEqual(plan.Saved.Source.Text, buffer.Capture().Source.Text);
        foreach (var image in new[] { first, second }) Assert.IsTrue(File.Exists(image.FullPath));
    }

    [TestMethod]
    public async Task Pasting_image_is_one_edit_and_retains_the_file_across_undo_and_redo()
    {
        using var scope = new Scope();
        var store = new ImageAttachmentStore(scope.Local);
        var image = await store.StorePngAsync(Png, scope.Document);
        var buffer = new StyledDocumentBuffer("前选中后", [new(0, 1)]);
        var before = buffer.Capture();
        var selection = Selection(before.Source, 1, 3);
        var history = new TextEditHistory(buffer);
        var edit = TextEditingOperations.Replace(buffer, selection, image.Markdown);
        store.Commit(image);
        history.Record(before, selection, edit, HistoryEditKind.Paste, DateTimeOffset.UtcNow);
        Assert.AreEqual("前" + image.Markdown + "后", buffer.Capture().Source.Text);
        history.Undo(); Assert.AreEqual(before.Source.Text, buffer.Capture().Source.Text);
        history.Redo(); Assert.AreEqual("前" + image.Markdown + "后", buffer.Capture().Source.Text);
        store.DiscardUncommitted(image); Assert.IsTrue(File.Exists(image.FullPath));
    }

    [TestMethod]
    public async Task Aborted_paste_removes_only_this_stores_uncommitted_generated_file()
    {
        using var scope = new Scope();
        var store = new ImageAttachmentStore(scope.Local);
        var image = await store.StorePngAsync(Png, null);
        var otherStore = new ImageAttachmentStore(scope.Local);
        otherStore.DiscardUncommitted(image); Assert.IsTrue(File.Exists(image.FullPath));
        store.DiscardUncommitted(image); Assert.IsFalse(File.Exists(image.FullPath));
        store.DiscardUncommitted(image);
    }

    [TestMethod]
    public async Task Invalid_or_oversized_or_cancelled_image_never_leaves_a_file()
    {
        using var scope = new Scope();
        var store = new ImageAttachmentStore(scope.Local);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.StorePngAsync([1, 2, 3], null));
        var large = new byte[ImageAttachmentStore.MaximumBytes + 1];
        Png.CopyTo(large, 0);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.StorePngAsync(large, null));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            store.StorePngAsync(Png, null, new CancellationToken(true)));
        Assert.AreEqual(0, Directory.EnumerateFiles(scope.Local, "*", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task Saving_and_reopening_keeps_markdown_and_portable_image_files()
    {
        using var scope = new Scope();
        var store = new ImageAttachmentStore(scope.Local);
        var image = await store.StorePngAsync(Png, null);
        var snapshot = Snapshot("中文 $\\frac{x+1}{\\sqrt{y+1}}$\r\n" + image.Markdown + "\r\n末尾");
        var path = Path.Combine(scope.Document, "with-image.md");
        var plan = await store.PrepareSaveAsync(snapshot, null, path);
        var files = new DocumentFileStore();
        await files.SaveAsync(path, plan.Saved.Source.Text, DocumentEncoding.Utf8Bom, null);
        var opened = await files.OpenAsync(path);
        Assert.AreEqual(plan.Saved.Source.Text, opened.Text);
        Assert.AreEqual(DocumentEncoding.Utf8Bom, opened.Encoding);
        var reference = MarkdownEditProjection.Create(MarkdownSyntaxParser.Parse(new(opened.Text, 1)))
            .References.Images.Single();
        CollectionAssert.AreEqual(Png, await File.ReadAllBytesAsync(Path.Combine(scope.Document, reference.Target)));
    }

    [TestMethod]
    public async Task Stale_save_plan_cannot_replace_new_typing_or_a_different_document_with_same_version()
    {
        using var scope = new Scope();
        var store = new ImageAttachmentStore(scope.Local);
        var image = await store.StorePngAsync(Png, null);
        var buffer = new StyledDocumentBuffer(image.Markdown, [new(0, 4)]);
        var before = buffer.Capture();
        var plan = await store.PrepareSaveAsync(before, null, Path.Combine(scope.Document, "race.md"));
        Assert.IsTrue(plan.CanApplyTo(before));
        Assert.IsFalse(plan.CanApplyTo(Snapshot(before.Source.Text)));
        buffer.Replace(new(before.Source.Length, 0), "新输入😀");
        Assert.IsFalse(plan.CanApplyTo(buffer.Capture()));
        Assert.AreEqual(before.Source.Text + "新输入😀", buffer.Capture().Source.Text);
        var session = new DocumentSessionState();
        session.Load(before.Source.Text, null, DocumentEncoding.Utf8, null);
        session.CompleteSave(session.Epoch, Path.Combine(scope.Document, "race.md"), DocumentEncoding.Utf8,
            plan.Saved.Source.Text, "SAVED", buffer.Capture().Source.Text);
        Assert.IsTrue(session.IsDirty);
    }

    [TestMethod]
    public async Task Missing_draft_attachment_blocks_save_instead_of_silently_creating_a_broken_link()
    {
        using var scope = new Scope();
        var store = new ImageAttachmentStore(scope.Local);
        var image = await store.StorePngAsync(Png, null);
        store.DiscardUncommitted(image);
        var source = Snapshot(image.Markdown);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            store.PrepareSaveAsync(source, null, Path.Combine(scope.Document, "note.md")));
        Assert.IsFalse(Directory.Exists(Path.Combine(scope.Document, "assets")));
    }

    [TestMethod]
    public async Task Manual_images_and_lookalike_names_are_never_migrated()
    {
        using var scope = new Scope();
        var snapshot = Snapshot("![a](assets/mdeditor-image-not-a-guid.png)\n![b](file:///C:/user.png)\n![c](https://example.invalid/image.png)");
        var plan = await new ImageAttachmentStore(scope.Local).PrepareSaveAsync(snapshot, null, Path.Combine(scope.Document, "note.md"));
        Assert.AreSame(snapshot, plan.Saved);
        Assert.IsFalse(Directory.Exists(Path.Combine(scope.Document, "assets")));
    }

    [TestMethod]
    [DataRow("mdeditor-image-.png", false)]
    [DataRow("mdeditor-image-00000000000000000000000000000000.png", true)]
    [DataRow("../mdeditor-image-00000000000000000000000000000000.png", false)]
    [DataRow("mdeditor-image-00000000000000000000000000000000.png/other", false)]
    public void Generated_names_cannot_escape_the_attachments_directory(string name, bool expected) =>
        Assert.AreEqual(expected, ImageAttachmentStore.IsGeneratedFileName(name));

    private static StyledDocumentSnapshot Snapshot(string text) => new StyledDocumentBuffer(text, [new(0, 4)]).Capture();
    private static TextSelection Selection(SourceTextSnapshot source, int start, int end) => new(
        new(TextSurface.Body, source.Version, start, CaretAffinity.Downstream),
        new(TextSurface.Body, source.Version, end, CaretAffinity.Upstream));

    private sealed class Scope : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("MDEditor-ImagePaste-").FullName;
        internal string Local { get; }
        internal string Document { get; }
        internal Scope()
        {
            Local = Directory.CreateDirectory(Path.Combine(Root, "本地 草稿 (1)")).FullName;
            Document = Directory.CreateDirectory(Path.Combine(Root, "中文 文档 (1)")).FullName;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
