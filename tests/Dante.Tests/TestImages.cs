using System.Buffers.Binary;

namespace Dante.Tests;

// Minimal image headers: enough for ImageInspector, which identifies the format and dimensions by content.
internal static class TestImages
{
    public static byte[] Png(int width, int height, int padding = 0)
    {
        var bytes = new byte[33 + padding];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    public static byte[] Gif(int width, int height)
    {
        var bytes = new byte[13];
        "GIF89a"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)height);
        return bytes;
    }

    // SOI, an APP0 segment to skip, then SOF0 with height before width.
    public static byte[] Jpeg(int width, int height)
    {
        byte[] bytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. new byte[14], 0xFF, 0xC0, 0x00, 0x11, 0x08, 0, 0, 0, 0,
            .. new byte[12]];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(25), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(27), (ushort)width);
        return bytes;
    }

    public static byte[] WebPExtended(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes);
        "WEBPVP8X"u8.CopyTo(bytes.AsSpan(8));
        bytes[24] = (byte)(width - 1); bytes[25] = (byte)((width - 1) >> 8); bytes[26] = (byte)((width - 1) >> 16);
        bytes[27] = (byte)(height - 1); bytes[28] = (byte)((height - 1) >> 8); bytes[29] = (byte)((height - 1) >> 16);
        return bytes;
    }
}
