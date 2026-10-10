using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public sealed record DecodedImage(byte[] Bytes, int Width, int Height);
public static class ImageDecoder
{
    public static DecodedImage Decode(byte[] bytes)
    {
        if (!OperatingSystem.IsWindows()) throw new UserFacingException("Image decoding requires Windows.");
        return DecodeWindows(bytes);
    }
    [SupportedOSPlatform("windows")]
    private static DecodedImage DecodeWindows(byte[] bytes)
    {
        try
        {
            // Clipboard DIBs can be larger than a compressed file. File tools enforce 10 MiB before decoding.
            if (bytes.Length > 128000128) throw new UserFacingException("Image input exceeds the decode limit.");
            if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))
                Dimensions(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16,4)), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20,4)));
            if (bytes.Length >= 54 && bytes[0] == 66 && bytes[1] == 77 && BitConverter.ToInt32(bytes,14) >= 40)
                Dimensions(BitConverter.ToInt32(bytes,18), Math.Abs((long)BitConverter.ToInt32(bytes,22)));
            if (bytes.Length >= 2 && bytes[0] == 255 && bytes[1] == 216) CheckJpegDimensions(bytes);
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream, false, true);
            Dimensions(image.Width, image.Height);
            if (image.RawFormat.Guid != ImageFormat.Png.Guid && image.RawFormat.Guid != ImageFormat.Jpeg.Guid && image.RawFormat.Guid != ImageFormat.Bmp.Guid)
                throw new UserFacingException("Supported images: PNG, JPEG and BMP.");
            using var png = new MemoryStream(); image.Save(png, ImageFormat.Png);
            if (png.Length > AttachmentFiles.MaxBytes) throw new UserFacingException("Normalized image exceeds 10 MiB.");
            return new(png.ToArray(), image.Width, image.Height);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        { throw new UserFacingException("File or clipboard content is not a supported image."); }
    }
    private static void Dimensions(long width, long height)
    { if (width <= 0 || height <= 0 || width * height > 32000000) throw new UserFacingException("Image exceeds 32 megapixels or has invalid dimensions."); }
    private static void CheckJpegDimensions(byte[] bytes)
    {
        var offset = 2;
        while (offset < bytes.Length)
        {
            if (bytes[offset++] != 255) break;
            while (offset < bytes.Length && bytes[offset] == 255) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xD9 or 0xDA) break;
            if (marker is 0x01 or >= 0xD0 and <= 0xD8) continue;
            if (offset + 2 > bytes.Length) break;
            var length = (bytes[offset] << 8) | bytes[offset + 1];
            if (length < 2 || offset + length > bytes.Length) break;
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (length < 8) break;
                Dimensions((bytes[offset+5] << 8) | bytes[offset+6], (bytes[offset+3] << 8) | bytes[offset+4]); return;
            }
            offset += length;
        }
        throw new UserFacingException("JPEG dimensions are missing or malformed.");
    }
}
