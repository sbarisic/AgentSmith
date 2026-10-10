using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AgentSmith.Core;

namespace AgentSmith.Cli;

internal sealed record ClipboardContent(string? Text = null, byte[]? Image = null, int Width = 0, int Height = 0);
internal interface IClipboardService { ClipboardContent Read(bool textOnly = false); }
internal sealed class WindowsClipboard : IClipboardService
{
    public ClipboardContent Read(bool textOnly = false)
    {
        if (!OperatingSystem.IsWindows()) throw new UserFacingException("Clipboard paste is supported on Windows. Use /attach for image files.");
        return ReadWindows(textOnly);
    }
    [SupportedOSPlatform("windows")]
    private static ClipboardContent ReadWindows(bool textOnly)
    {
        if (!OpenClipboard(IntPtr.Zero)) throw new UserFacingException("Clipboard is busy. Your draft is unchanged; try Alt+V again.");
        try
        {
            var pngFormat = RegisterClipboardFormat("PNG");
            if (!textOnly && IsClipboardFormatAvailable(pngFormat)) return Decode(Copy(pngFormat));
            if (!textOnly && IsClipboardFormatAvailable(8))
            {
                var dib = Copy(8);
                if (dib.Length < 40) throw new UserFacingException("Invalid clipboard bitmap.");
                var header = BitConverter.ToInt32(dib, 0); var bits = BitConverter.ToUInt16(dib, 14); var compression = BitConverter.ToInt32(dib, 16);
                var palette = BitConverter.ToInt32(dib, 32);
                if (header < 40 || header > dib.Length || palette < 0 || palette > 256) throw new UserFacingException("Unsupported clipboard bitmap.");
                var offset = checked(14 + header + (palette > 0 ? palette * 4 : bits <= 8 ? (1 << bits) * 4 : 0) + (header == 40 && compression == 3 ? 12 : 0));
                using var bmp = new MemoryStream(); using (var writer = new BinaryWriter(bmp, System.Text.Encoding.UTF8, true))
                { writer.Write((ushort)0x4D42); writer.Write(checked(dib.Length + 14)); writer.Write(0); writer.Write(offset); writer.Write(dib); }
                return Decode(bmp.ToArray());
            }
            if (!IsClipboardFormatAvailable(13)) throw new UserFacingException(textOnly ? "Clipboard has no text. Images belong in the chat composer." : "Clipboard has no supported image or Unicode text.");
            var bytes = Copy(13); var text = System.Text.Encoding.Unicode.GetString(bytes); var nul = text.IndexOf('\0');
            return new(Text: nul < 0 ? text : text[..nul]);
        }
        finally { CloseClipboard(); }
    }
    private static byte[] Copy(uint format)
    {
        var handle = GetClipboardData(format); var size = GlobalSize(handle).ToUInt64();
        // DIB can be larger than encoded PNG, but decoded pixel count is still bounded below.
        if (handle == IntPtr.Zero || size == 0 || size > 128000128) throw new UserFacingException("Clipboard content is unavailable or too large.");
        var pointer = GlobalLock(handle); if (pointer == IntPtr.Zero) throw new UserFacingException("Clipboard content could not be read.");
        try { var bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
        finally { GlobalUnlock(handle); }
    }
    public static ClipboardContent ReadFile(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new UserFacingException("Image decoding is supported on Windows.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > AttachmentFiles.MaxBytes) throw new UserFacingException("Image exceeds 10 MiB.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return Decode(bytes);
    }
    [SupportedOSPlatform("windows")]
    private static ClipboardContent Decode(byte[] bytes)
    {
        var image = AgentSmith.Tools.ImageDecoder.Decode(bytes);
        return new(Image: image.Bytes, Width: image.Width, Height: image.Height);
    }
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
}
