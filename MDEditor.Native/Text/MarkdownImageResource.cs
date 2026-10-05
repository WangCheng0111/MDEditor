using Microsoft.Graphics.Canvas;

namespace MDEditor.Native.Text;

public enum MarkdownImageStatus { Loading, Ready, Missing, Unsupported, Failed }

/// <summary>One device-bound image result; the editor owns and disposes its bitmap.</summary>
public sealed record MarkdownImageResource(string Target, MarkdownImageStatus Status,
    CanvasBitmap? Bitmap = null, string Message = "")
{
    public static MarkdownImageResource Loading(string target) =>
        new(target, MarkdownImageStatus.Loading, null, "加载中");
}

public sealed record MarkdownImageAtom(MDEditor.Core.Text.SourceRange Source,
    string Alternative, MarkdownImageResource Resource);
