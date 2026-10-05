using System.Security.Cryptography;
using System.Text;
using MDEditor.Core.Text;

namespace MDEditor.Core.Documents;

public enum DocumentEncoding { Utf8, Utf8Bom, Utf16Le, Utf16Be }

public sealed record OpenedDocument(string Text, DocumentEncoding Encoding, string Fingerprint);

public sealed class DocumentChangedOnDiskException(string path) : IOException(
    $"磁盘文件已在编辑器外被修改：{path}。请使用“另存为”保留当前编辑，或重新打开磁盘版本。");

/// <summary>Strict, lossless Markdown file I/O. Save publishes a complete sibling file atomically.</summary>
public sealed class DocumentFileStore
{
    public const long MaximumBytes = 32L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictLe = new(false, true, true);
    private static readonly UnicodeEncoding StrictBe = new(true, true, true);

    public static OpenedDocument Decode(ReadOnlySpan<byte> bytes)
    {
        DocumentEncoding format;
        int offset;
        Encoding encoding;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            (format, offset, encoding) = (DocumentEncoding.Utf8Bom, 3, StrictUtf8);
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            (format, offset, encoding) = (DocumentEncoding.Utf16Le, 2, StrictLe);
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            (format, offset, encoding) = (DocumentEncoding.Utf16Be, 2, StrictBe);
        else (format, offset, encoding) = (DocumentEncoding.Utf8, 0, StrictUtf8);
        var text = encoding.GetString(bytes[offset..]);
        _ = new DocumentTextBuffer(text);
        if (text.Any(character => character is '\0' or '\t' or '\u2028' or '\u2029' or '\u00AD' ||
            char.IsControl(character) && character is not ('\r' or '\n')))
            throw new InvalidDataException("此文件含有当前原生排版尚不支持的控制字符、Tab 或软连字符；原文件未被修改。");
        return new(text, format, Fingerprint(bytes));
    }

    public static byte[] Encode(string text, DocumentEncoding format)
    {
        ArgumentNullException.ThrowIfNull(text);
        _ = new DocumentTextBuffer(text);
        Encoding encoding = format switch
        {
            DocumentEncoding.Utf8 or DocumentEncoding.Utf8Bom => StrictUtf8,
            DocumentEncoding.Utf16Le => StrictLe,
            DocumentEncoding.Utf16Be => StrictBe,
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        var body = encoding.GetBytes(text);
        byte[] preamble = format switch
        {
            DocumentEncoding.Utf8 => [],
            DocumentEncoding.Utf8Bom => [0xEF, 0xBB, 0xBF],
            _ => encoding.GetPreamble()
        };
        if (preamble.Length == 0) return body;
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0); body.CopyTo(result, preamble.Length);
        return result;
    }

    public async Task<OpenedDocument> OpenAsync(string path, CancellationToken token = default)
    {
        var full = Path.GetFullPath(path);
        var file = new FileInfo(full);
        if (file.Length > MaximumBytes) throw new InvalidDataException("文件超过 32 MiB 上限，未打开。");
        var bytes = await File.ReadAllBytesAsync(full, token);
        if (bytes.LongLength > MaximumBytes) throw new InvalidDataException("文件超过 32 MiB 上限，未打开。");
        return Decode(bytes);
    }

    /// <param name="expectedFingerprint">Loaded/saved bytes for overwrite conflict detection; null for a picker-confirmed Save As.</param>
    public async Task<string> SaveAsync(string path, string text, DocumentEncoding encoding,
        string? expectedFingerprint, CancellationToken token = default)
    {
        var full = Path.GetFullPath(path);
        var bytes = Encode(text, encoding);
        if (bytes.LongLength > MaximumBytes) throw new InvalidDataException("文档超过 32 MiB 上限，未保存。");
        if (expectedFingerprint is not null)
        {
            if (!File.Exists(full) || Fingerprint(await File.ReadAllBytesAsync(full, token)) != expectedFingerprint)
                throw new DocumentChangedOnDiskException(full);
        }
        await AtomicDocumentFile.WriteAsync(full, bytes, token, expectedFingerprint);
        return Fingerprint(bytes);
    }

    public static string Fingerprint(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

internal static class AtomicDocumentFile
{
    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken token,
        string? expectedFingerprint = null)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("文件路径缺少目录。", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, token);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (expectedFingerprint is not null && (!File.Exists(path) ||
                DocumentFileStore.Fingerprint(await File.ReadAllBytesAsync(path, token)) != expectedFingerprint))
                throw new DocumentChangedOnDiskException(path);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
