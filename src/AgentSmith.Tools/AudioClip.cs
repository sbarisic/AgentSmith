using System.Buffers.Binary;
using AgentSmith.Core;

namespace AgentSmith.Tools;

public sealed record AudioClip(byte[] Bytes, string Format, double StartSeconds, double? DurationSeconds, double? SourceDurationSeconds, bool Partial)
{
    public static AudioClip Prepare(byte[] source, string extension, int start, int duration)
    {
        if (start < 0 || duration is < 0 or > 120) throw new UserFacingException("Invalid audio time range.");
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            if (start != 0 || duration != 0) throw new UserFacingException("MP3 time slicing is unsupported. Use startSeconds=0 and durationSeconds=0, or convert the selected clip to PCM WAV.");
            if (source.Length < 12 || source.Length > AudioTool.MaxClipBytes ||
                !(source.AsSpan(0, 3).SequenceEqual("ID3"u8) || source[0] == 0xff && (source[1] & 0xe0) == 0xe0))
                throw new UserFacingException("MP3 must have an MPEG/ID3 header and be at most 10 MiB. Decoding is validated by the audio provider.");
            return new(source, "mp3", 0, null, null, false);
        }
        if (!extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) || source.Length < 44 || source.Length > AudioTool.MaxFileBytes ||
            !source.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !source.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new UserFacingException("Expected a valid RIFF PCM WAV file.");
        uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, 4));
        ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset, 2));
        var end = 8L + U32(4);
        if (end != source.Length) throw new UserFacingException("WAV length does not match its RIFF header.");
        var fmt = -1; var data = -1; var dataLength = 0;
        for (long offset = 12; offset < end;)
        {
            if (offset + 8 > end) throw new UserFacingException("WAV has a truncated chunk header.");
            var p = (int)offset; var size = U32(p + 4); var next = offset + 8 + size + (size & 1);
            if (next > end) throw new UserFacingException("WAV has a truncated chunk.");
            if (source.AsSpan(p, 4).SequenceEqual("fmt "u8))
            { if (fmt >= 0 || size < 16) throw new UserFacingException("WAV has invalid format chunks."); fmt = p + 8; }
            if (source.AsSpan(p, 4).SequenceEqual("data"u8))
            { if (data >= 0) throw new UserFacingException("WAV has multiple data chunks."); data = p + 8; dataLength = (int)size; }
            offset = next;
        }
        if (fmt < 0 || data < 0 || U16(fmt) != 1) throw new UserFacingException("Only uncompressed integer PCM WAV is supported; convert float, extensible or compressed WAV first.");
        var channels = U16(fmt + 2); var rate = U32(fmt + 4); var bits = U16(fmt + 14); var align = U16(fmt + 12);
        if (channels is < 1 or > 2 || rate is < 8000 or > 192000 || bits is not (8 or 16 or 24 or 32) ||
            align != channels * bits / 8 || U32(fmt + 8) != rate * align || dataLength == 0 || dataLength % align != 0)
            throw new UserFacingException("WAV requires valid mono/stereo PCM, 8..192 kHz and 8/16/24/32-bit samples.");
        var frames = dataLength / align; var first = (long)start * rate;
        if (first >= frames) throw new UserFacingException("Audio start is past the end of the file.");
        var selected = Math.Min(frames - first, (long)(duration == 0 ? 120 : duration) * rate);
        // Normalize to 16-bit PCM so the provider receives one consistent WAV format.
        var outputLength = selected * channels * 2;
        if (outputLength + 44 > AudioTool.MaxClipBytes) throw new UserFacingException("Selected WAV clip exceeds 10 MiB. Request a shorter duration.");
        var output = new byte[(int)outputLength + 44];
        using (var writer = new BinaryWriter(new MemoryStream(output)))
        {
            writer.Write("RIFF"u8); writer.Write(output.Length - 8); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((ushort)1); writer.Write(channels); writer.Write(rate); writer.Write(rate * channels * 2);
            writer.Write((ushort)(channels * 2)); writer.Write((ushort)16); writer.Write("data"u8); writer.Write((int)outputLength);
            var pos = data + (int)(first * align);
            for (long sample = 0; sample < selected * channels; sample++, pos += bits / 8)
            {
                short value = bits switch {
                    8 => (short)((source[pos] - 128) << 8),
                    16 => BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(pos, 2)),
                    24 => BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(pos + 1, 2)),
                    _ => (short)(BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(pos, 4)) >> 16)
                };
                writer.Write(value);
            }
        }
        return new(output, "wav", start, (double)selected / rate, (double)frames / rate, first != 0 || selected != frames);
    }
}
