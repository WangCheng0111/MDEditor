using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MDEditor.Native.Text;
using Microsoft.Graphics.Canvas;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    private readonly Dictionary<string, MarkdownImageResource> _imageResources = new(StringComparer.Ordinal);
    private string? _documentDirectory;
    private long _imageGeneration;

    private void RequestImageResources()
    {
        if (_disposed || _device is not { } device) return;
        foreach (var image in CurrentPresentation().Text.References.Images)
        {
            if (_imageResources.ContainsKey(image.Target)) continue;
            _imageResources.Add(image.Target, MarkdownImageResource.Loading(image.Target));
            _ = LoadImageAsync(image.Target, device, _imageGeneration);
        }
    }

    private async Task LoadImageAsync(string target, CanvasDevice device, long generation)
    {
        // Keep malformed addresses and missing files on the same asynchronous path as
        // successful loads; neither editing nor initial layout waits for disk or decoding.
        await Task.Yield();
        MarkdownImageResource result;
        try
        {
            if (string.IsNullOrWhiteSpace(target))
                result = new(target, MarkdownImageStatus.Missing, null, "地址为空");
            else if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                result = new(target, MarkdownImageStatus.Unsupported, null, "当前仅加载本地图片");
            else
            {
                var path = ResolveImagePath(target);
                var file = new FileInfo(path);
                if (!file.Exists)
                    result = new(target, MarkdownImageStatus.Missing, null, "文件不存在");
                else if (file.Length > 16 * 1024 * 1024)
                    result = new(target, MarkdownImageStatus.Unsupported, null, "图片超过 16 MiB 限制");
                else
                {
                    var bitmap = await CanvasBitmap.LoadAsync(device, path);
                    var size = bitmap.SizeInPixels;
                    if (size.Width <= 0 || size.Height <= 0 ||
                        (double)size.Width * size.Height > 40_000_000)
                    {
                        bitmap.Dispose();
                        result = new(target, MarkdownImageStatus.Unsupported, null, "图片尺寸超出限制");
                    }
                    else result = new(target, MarkdownImageStatus.Ready, bitmap);
                }
            }
        }
        catch (Exception error)
        {
            result = new(target, MarkdownImageStatus.Failed, null,
                $"解码失败：{error.GetType().Name}");
        }
        if (_disposed || !ReferenceEquals(_device, device) || generation != _imageGeneration)
        {
            result.Bitmap?.Dispose();
            return;
        }
        _imageResources[target] = result;
        _renderer?.SetContent(EditableContent());
        RequestLayout(force: true, immediate: true);
        UpdateStatusIfReady();
    }

    private string ResolveImagePath(string target)
    {
        if (Path.IsPathRooted(target)) return Path.GetFullPath(target);
        var directory = _documentDirectory ?? AppContext.BaseDirectory;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme.Equals("ms-appx", StringComparison.OrdinalIgnoreCase))
            {
                target = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
                directory = AppContext.BaseDirectory;
            }
            else if (uri.IsFile) return Path.GetFullPath(uri.LocalPath);
            else throw new NotSupportedException("Unsupported image URI scheme.");
        }
        return Path.GetFullPath(Path.Combine(directory,
            Uri.UnescapeDataString(target).Replace('/', Path.DirectorySeparatorChar)));
    }

    private void ReleaseImageResources()
    {
        foreach (var image in _imageResources.Values)
            image.Bitmap?.Dispose();
        _imageResources.Clear();
    }

    private string? ImageDiagnosticStatus()
    {
        var images = CurrentPresentation().Text.References.Images;
        var failures = images.Where(image => _imageResources.TryGetValue(image.Target, out var resource) &&
            resource.Status is MarkdownImageStatus.Missing or MarkdownImageStatus.Unsupported or
                MarkdownImageStatus.Failed).ToArray();
        if (failures.Length == 0) return null;
        var caret = _selection?.Focus.Offset ?? -1;
        var selected = failures.FirstOrDefault(image => image.Source.Start <= caret && caret <= image.Source.End)
            ?? failures[0];
        return $" · 图片不可用：{_imageResources[selected.Target].Message}（{selected.Target}）；源码已保留";
    }
}
