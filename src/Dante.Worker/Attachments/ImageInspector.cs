using System.Buffers.Binary;

namespace Dante.Worker.Attachments;

public sealed record ImageInfo(string MediaType, string Extension, int Width, int Height);

// Identifies an image by its bytes, never by the name or MIME type the sender declared (AD-29). Only the formats both
// CLIs accept are recognised: JPEG, PNG, GIF and WebP.
public static class ImageInspector
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static ImageInfo? Inspect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[30];
        var length = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        header = header[..length];

        if (length >= 24 && header[..8].SequenceEqual(PngSignature) && header[12..16].SequenceEqual("IHDR"u8))
            return Image("image/png", ".png", BinaryPrimitives.ReadInt32BigEndian(header[16..]),
                BinaryPrimitives.ReadInt32BigEndian(header[20..]));
        if (length >= 10 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)))
            return Image("image/gif", ".gif", BinaryPrimitives.ReadUInt16LittleEndian(header[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(header[8..]));
        if (length >= 30 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            return WebP(header);
        if (length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return Jpeg(stream);
        return null;
    }

    private static ImageInfo? WebP(ReadOnlySpan<byte> header)
    {
        var chunk = header[12..16];
        if (chunk.SequenceEqual("VP8 "u8))
            return Image("image/webp", ".webp", BinaryPrimitives.ReadUInt16LittleEndian(header[26..]) & 0x3FFF,
                BinaryPrimitives.ReadUInt16LittleEndian(header[28..]) & 0x3FFF);
        if (chunk.SequenceEqual("VP8L"u8) && header[20] == 0x2F)
            return Image("image/webp", ".webp", 1 + (header[21] | (header[22] & 0x3F) << 8),
                1 + (header[22] >> 6 | header[23] << 2 | (header[24] & 0x0F) << 10));
        if (chunk.SequenceEqual("VP8X"u8))
            return Image("image/webp", ".webp", 1 + (header[24] | header[25] << 8 | header[26] << 16),
                1 + (header[27] | header[28] << 8 | header[29] << 16));
        return null;
    }

    // Walks the segments up to the first start-of-frame marker, which carries the dimensions.
    private static ImageInfo? Jpeg(FileStream stream)
    {
        stream.Position = 2;
        Span<byte> segment = stackalloc byte[9];
        while (stream.ReadAtLeast(segment[..4], 4, throwOnEndOfStream: false) == 4)
        {
            if (segment[0] != 0xFF) return null;
            var marker = segment[1];
            if (marker == 0xFF) { stream.Position -= 3; continue; }
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) { stream.Position -= 2; continue; }
            var size = BinaryPrimitives.ReadUInt16BigEndian(segment[2..]);
            if (size < 2) return null;
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (stream.ReadAtLeast(segment[..5], 5, throwOnEndOfStream: false) < 5) return null;
                return Image("image/jpeg", ".jpg", BinaryPrimitives.ReadUInt16BigEndian(segment[3..]),
                    BinaryPrimitives.ReadUInt16BigEndian(segment[1..]));
            }
            if (marker is 0xD9 or 0xDA) return null;
            stream.Position += size - 2;
        }
        return null;
    }

    private static ImageInfo? Image(string mediaType, string extension, int width, int height) =>
        width > 0 && height > 0 ? new ImageInfo(mediaType, extension, width, height) : null;
}
