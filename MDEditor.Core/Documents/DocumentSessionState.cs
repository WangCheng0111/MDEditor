namespace MDEditor.Core.Documents;

/// <summary>Tracks the saved source, not layout/zoom; a save may complete after newer edits.</summary>
public sealed class DocumentSessionState
{
    private string? _savedText;
    public long Epoch { get; private set; }
    public string? FilePath { get; private set; }
    public DocumentEncoding Encoding { get; private set; } = DocumentEncoding.Utf8;
    public string? SavedFingerprint { get; private set; }
    public bool IsDirty { get; private set; }

    public void Load(string text, string? filePath, DocumentEncoding encoding,
        string? fingerprint, bool recovered = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        Epoch++;
        FilePath = filePath;
        Encoding = encoding;
        SavedFingerprint = fingerprint;
        _savedText = recovered ? null : text;
        IsDirty = recovered;
    }

    public void Edited(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        IsDirty = _savedText is null || !string.Equals(_savedText, text, StringComparison.Ordinal);
    }

    public bool CompleteSave(long epoch, string path, DocumentEncoding encoding,
        string savedText, string fingerprint, string currentText)
    {
        if (epoch != Epoch) return false;
        FilePath = path; Encoding = encoding; SavedFingerprint = fingerprint;
        _savedText = savedText;
        Edited(currentText);
        return true;
    }
}
