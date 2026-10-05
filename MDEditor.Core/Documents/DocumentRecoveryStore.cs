using System.Text;
using System.Text.Json;
using MDEditor.Core.Text;

namespace MDEditor.Core.Documents;

/// <summary>A private, checksummed recovery checkpoint; never replaces the user's Markdown file.</summary>
public sealed record RecoveryCheckpoint(string Text, DocumentStyleMarker[] Styles, string? FilePath,
    DocumentEncoding Encoding, string? SavedFingerprint, DateTimeOffset RecordedAt);

public sealed class DocumentRecoveryStore(string directory)
{
    private sealed record Envelope(int Schema, string Payload, string Fingerprint);
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly string _path = Path.Combine(Path.GetFullPath(directory), "active-document.recovery.json");

    public async Task WriteAsync(RecoveryCheckpoint checkpoint, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var payload = JsonSerializer.Serialize(checkpoint);
        var envelope = JsonSerializer.Serialize(new Envelope(1, payload,
            DocumentFileStore.Fingerprint(Encoding.UTF8.GetBytes(payload))));
        var bytes = Encoding.UTF8.GetBytes(envelope);
        if (bytes.LongLength > DocumentFileStore.MaximumBytes * 2)
            throw new InvalidDataException("恢复检查点超过上限。");
        await _serial.WaitAsync(token);
        try { await AtomicDocumentFile.WriteAsync(_path, bytes, token); }
        finally { _serial.Release(); }
    }

    public async Task<RecoveryCheckpoint?> ReadAsync(CancellationToken token = default)
    {
        await _serial.WaitAsync(token);
        try
        {
            if (!File.Exists(_path)) return null;
            var file = new FileInfo(_path);
            if (file.Length > DocumentFileStore.MaximumBytes * 2)
                throw new InvalidDataException("恢复检查点超过上限；文件仍保留在应用目录。");
            var bytes = await File.ReadAllBytesAsync(_path, token);
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes)
                ?? throw new InvalidDataException("恢复检查点无效；文件仍保留在应用目录。");
            if (envelope.Schema != 1 || envelope.Payload is null ||
                DocumentFileStore.Fingerprint(Encoding.UTF8.GetBytes(envelope.Payload)) != envelope.Fingerprint)
                throw new InvalidDataException("恢复检查点校验失败；文件仍保留在应用目录。");
            var result = JsonSerializer.Deserialize<RecoveryCheckpoint>(envelope.Payload)
                ?? throw new InvalidDataException("恢复检查点内容无效；文件仍保留在应用目录。");
            _ = new StyledDocumentBuffer(result.Text, result.Styles);
            if (!Enum.IsDefined(result.Encoding))
                throw new InvalidDataException("恢复检查点编码无效；文件仍保留在应用目录。");
            return result;
        }
        finally { _serial.Release(); }
    }

    public async Task ClearAsync(CancellationToken token = default)
    {
        await _serial.WaitAsync(token);
        try { if (File.Exists(_path)) File.Delete(_path); }
        finally { _serial.Release(); }
    }
}
