using MDEditor.Core.Markdown;
using MDEditor.Core.Text;
using System.Collections.Concurrent;

namespace MDEditor.Core.Documents;

public sealed record StoredImageAttachment(string FullPath, string Target)
{
    public string Markdown => $"![image]({Target})";
}

public sealed record ImageAttachmentRewrite(SourceRange Range, string Target);

/// <summary>A source-versioned save plan. Only this application's generated image destinations change.</summary>
public sealed class ImageAttachmentSavePlan
{
    public StyledDocumentSnapshot Before { get; }
    public StyledDocumentSnapshot Saved { get; }
    public IReadOnlyList<ImageAttachmentRewrite> Rewrites { get; }
    public bool Changed => Rewrites.Count != 0;
    public bool CanApplyTo(StyledDocumentSnapshot current) =>
        ReferenceEquals(Before.Source, current.Source) && Before.Styles.SequenceEqual(current.Styles);
    public SourceRange ChangedRange => Changed
        ? new(Rewrites[0].Range.Start, Rewrites[^1].Range.End - Rewrites[0].Range.Start) : new(0, 0);

    internal ImageAttachmentSavePlan(StyledDocumentSnapshot before, List<ImageAttachmentRewrite> rewrites)
    {
        Before = before;
        Rewrites = rewrites.AsReadOnly();
        if (!Changed) { Saved = before; return; }
        var buffer = new StyledDocumentBuffer(before.Source.Text, before.Styles, before.Source.Version);
        foreach (var rewrite in rewrites.AsEnumerable().Reverse()) buffer.Replace(rewrite.Range, rewrite.Target);
        Saved = buffer.Capture();
    }

    public int MapOffset(int offset)
    {
        if (offset < 0 || offset > Before.Source.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var delta = 0;
        foreach (var rewrite in Rewrites)
        {
            if (offset <= rewrite.Range.Start) break;
            if (offset < rewrite.Range.End)
                return rewrite.Range.Start + delta + Math.Min(offset - rewrite.Range.Start, rewrite.Target.Length);
            delta += rewrite.Target.Length - rewrite.Range.Length;
        }
        return offset + delta;
    }

    public string ChangedText => Changed ? Saved.Source.Text.Substring(ChangedRange.Start,
        MapOffset(ChangedRange.End) - ChangedRange.Start) : "";
}

/// <summary>Durable PNG attachments, never clipboard/temp-file references. No network I/O or overwrites.</summary>
public sealed class ImageAttachmentStore
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public const long MaximumPixels = 40_000_000;
    public const string AssetsDirectoryName = "assets";
    private const string Prefix = "mdeditor-image-";
    private readonly string _draftDirectory;
    private readonly ConcurrentDictionary<string, byte> _uncommitted = new(StringComparer.OrdinalIgnoreCase);

    public ImageAttachmentStore(string localDirectory) =>
        _draftDirectory = Path.Combine(Path.GetFullPath(localDirectory), "ClipboardImages");

    public static bool IsGeneratedFileName(string name) =>
        name.StartsWith(Prefix, StringComparison.Ordinal) && name.EndsWith(".png", StringComparison.Ordinal) &&
        Guid.TryParseExact(name.AsSpan(Prefix.Length, name.Length - Prefix.Length - 4), "N", out _);

    public async Task<StoredImageAttachment> StorePngAsync(byte[] png, string? documentDirectory,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < 8 || png.Length > MaximumBytes ||
            !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("图片不是有效 PNG 或超过 16 MiB 限制。");
        var directory = documentDirectory is null ? _draftDirectory :
            Path.Combine(Path.GetFullPath(documentDirectory), AssetsDirectoryName);
        var name = $"{Prefix}{Guid.NewGuid():N}.png";
        var path = Path.Combine(directory, name);
        await WriteNewAsync(path, png, token).ConfigureAwait(false);
        _uncommitted.TryAdd(path, 0);
        var target = documentDirectory is null ? new Uri(path).AbsoluteUri.Replace("(", "%28").Replace(")", "%29") :
            $"{AssetsDirectoryName}/{name}";
        return new(path, target);
    }

    public void Commit(StoredImageAttachment attachment) => _uncommitted.TryRemove(attachment.FullPath, out _);

    /// <summary>Only called for a newly created attachment whose insertion never committed.</summary>
    public void DiscardUncommitted(StoredImageAttachment attachment)
    {
        if (!_uncommitted.TryRemove(attachment.FullPath, out _)) return;
        File.Delete(attachment.FullPath);
    }

    public async Task<ImageAttachmentSavePlan> PrepareSaveAsync(StyledDocumentSnapshot snapshot,
        string? oldDirectory, string documentPath, CancellationToken token = default)
    {
        if (!snapshot.Source.Text.Contains(Prefix, StringComparison.Ordinal)) return new(snapshot, []);
        var nextDirectory = Path.GetDirectoryName(Path.GetFullPath(documentPath))!;
        var nextAssets = Path.Combine(nextDirectory, AssetsDirectoryName);
        var syntax = MarkdownSyntaxParser.Parse(snapshot.Source);
        var references = MarkdownReferenceIndex.Create(snapshot.Source, syntax,
            MarkdownMathSyntax.Parse(snapshot.Source, syntax));
        var rewrites = new List<ImageAttachmentRewrite>();
        var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in references.Images.OrderBy(image => image.Source.Start))
        {
            token.ThrowIfCancellationRequested();
            if (!TryOwnedPath(image.Target, oldDirectory, out var original) ||
                !TryDestinationRange(snapshot.Source, image, out var range)) continue;
            if (!copied.TryGetValue(original, out var target))
            {
                var destination = Path.Combine(nextAssets, Path.GetFileName(original));
                if (!string.Equals(original, destination, StringComparison.OrdinalIgnoreCase))
                {
                    var info = new FileInfo(original);
                    if (!info.Exists) throw new FileNotFoundException("粘贴的图片附件丢失，文档未保存。", original);
                    if (info.Length > MaximumBytes) throw new InvalidDataException("图片附件超过 16 MiB 限制。");
                    var bytes = await File.ReadAllBytesAsync(original, token).ConfigureAwait(false);
                    if (bytes.Length > MaximumBytes) throw new InvalidDataException("图片附件超过 16 MiB 限制。");
                    if (File.Exists(destination) && (new FileInfo(destination).Length > MaximumBytes ||
                        !(await File.ReadAllBytesAsync(destination, token).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes)))
                        destination = Path.Combine(nextAssets, $"{Prefix}{Guid.NewGuid():N}.png");
                    if (!File.Exists(destination)) await WriteNewAsync(destination, bytes, token).ConfigureAwait(false);
                }
                target = $"{AssetsDirectoryName}/{Path.GetFileName(destination)}";
                copied.Add(original, target);
            }
            if (image.Target != target) rewrites.Add(new(range, target));
        }
        return new(snapshot, rewrites);
    }

    private bool TryOwnedPath(string target, string? directory, out string path)
    {
        path = "";
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile) return false;
            path = Path.GetFullPath(uri.LocalPath);
            if (!string.Equals(Path.GetDirectoryName(path), _draftDirectory, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        else
        {
            if (directory is null || !target.StartsWith(AssetsDirectoryName + "/", StringComparison.Ordinal)) return false;
            var name = target[(AssetsDirectoryName.Length + 1)..];
            if (!IsGeneratedFileName(name)) return false;
            path = Path.Combine(Path.GetFullPath(directory), AssetsDirectoryName, name);
        }
        return IsGeneratedFileName(Path.GetFileName(path));
    }

    private static bool TryDestinationRange(SourceTextSnapshot source, MarkdownImageReference image,
        out SourceRange range)
    {
        range = default;
        var raw = source.GetText(image.Source);
        var start = raw.IndexOf("](", StringComparison.Ordinal);
        if (start < 0) return false; // Reference definitions are not generated by image paste.
        start += 2;
        if (start < raw.Length && raw[start] == '<') start++;
        if (start + image.Target.Length > raw.Length ||
            !raw.AsSpan(start, image.Target.Length).SequenceEqual(image.Target)) return false;
        range = new(image.Source.Start + start, image.Target.Length);
        return true;
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var created = false;
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            created = true;
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            stream.Flush(true);
        }
        catch
        {
            if (created) File.Delete(path);
            throw;
        }
    }
}
