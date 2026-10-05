using System;
using System.Threading.Tasks;
using MDEditor.Core.Documents;
using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;
using Windows.Storage;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    private ImageAttachmentStore? _imageAttachments;
    private ImageAttachmentStore ImageAttachments => _imageAttachments ??=
        new(ApplicationData.Current.LocalFolder.Path);
    internal bool ImagePasteSuspended { get; set; }

    internal Task<ImageAttachmentSavePlan> PrepareImageAttachmentsForSaveAsync(
        StyledDocumentSnapshot snapshot, string path)
    {
        var store = ImageAttachments;
        var directory = _documentDirectory;
        return Task.Run(() => store.PrepareSaveAsync(snapshot, directory, path));
    }

    internal void CommitSavedImageAttachments(ImageAttachmentSavePlan plan)
    {
        // A background save must not overwrite subsequent typing or a live IME composition.
        if (!plan.Changed || _disposed || _imeComposing ||
            !plan.CanApplyTo(_editorText.Capture())) return;
        var selection = CurrentEditableSelection();
        var body = selection ?? new TextSelection(
            new(TextSurface.Body, plan.Before.Source.Version, 0, CaretAffinity.Downstream),
            new(TextSurface.Body, plan.Before.Source.Version, 0, CaretAffinity.Downstream));
        var reveal = CurrentPresentation().Text.RevealedAtSourceOffset;
        _history.BreakCoalescing();
        var change = _editorText.Restore(plan.ChangedRange, plan.Before.Source.GetText(plan.ChangedRange),
            plan.ChangedText, plan.Saved.Styles, plan.Before.Source.Version);
        var next = new TextSelection(
            body.Anchor with { SourceVersion = change.NewVersion, Offset = plan.MapOffset(body.Anchor.Offset) },
            body.Focus with { SourceVersion = change.NewVersion, Offset = plan.MapOffset(body.Focus.Offset) });
        _livePresentation = MarkdownRichTextProjection.FromSyntax(
            MarkdownSyntaxParser.Parse(_editorText.Capture().Source),
            reveal is { } offset ? plan.MapOffset(offset) : null, SearchViewModel.IsSourceMode);
        ApplyEdit(new(change, next), plan.Before, body, HistoryEditKind.Replace);
    }
}
