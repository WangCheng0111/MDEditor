using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MDEditor.Core.Documents;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MDEditor.Services;

/// <summary>Windows clipboard/WIC adapter. Returns a real PNG, retaining alpha and display orientation.</summary>
internal static class ClipboardImageReader
{
    public static async Task<byte[]?> ReadPngAsync(DataPackageView content)
    {
        if (content.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await content.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            return await EncodePngAsync(stream);
        }
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            var files = (await content.GetStorageItemsAsync()).OfType<StorageFile>().ToArray();
            if (files.Length != 1 || !IsImageExtension(files[0].FileType)) return null;
            using var stream = await files[0].OpenReadAsync();
            return await EncodePngAsync(stream);
        }
        return null;
    }

    private static bool IsImageExtension(string extension) => extension.ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" or ".ico";

    private static async Task<byte[]> EncodePngAsync(IRandomAccessStream input)
    {
        // Clipboard bitmaps may be uncompressed BMP (e.g. a 4K screenshot), unlike the stored PNG limit.
        if (input.Size > (ulong)(ImageAttachmentStore.MaximumPixels * 4 + 64 * 1024))
            throw new InvalidDataException("剪贴板图像数据过大。");
        var decoder = await BitmapDecoder.CreateAsync(input);
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 ||
            (long)decoder.PixelWidth * decoder.PixelHeight > ImageAttachmentStore.MaximumPixels)
            throw new InvalidDataException("图片尺寸超过 4000 万像素限制。");
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight, new BitmapTransform(), ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        if (output.Size > ImageAttachmentStore.MaximumBytes)
            throw new InvalidDataException("转换后的 PNG 超过 16 MiB 限制。");
        output.Seek(0);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        var bytes = new byte[(int)output.Size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
