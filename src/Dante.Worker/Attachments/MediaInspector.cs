namespace Dante.Worker.Attachments;

public sealed record MediaInfo(string MediaType, string Extension);

// Identifies an audio or video container by its bytes, never by the name or MIME type the sender declared (#96,
// AD-29). Only file containers are recognised: playlists and other text formats that make ffmpeg open further files
// or URLs never get this far. The streams inside are checked by ffprobe when the media is prepared.
public static class MediaInspector
{
    public static MediaInfo? Inspect(string path, AttachmentKind kind)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[12];
        var length = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        header = header[..length];
        var audio = kind == AttachmentKind.Audio;

        if (length >= 4 && header[..4].SequenceEqual("OggS"u8))
            return audio ? new MediaInfo("audio/ogg", ".ogg") : new MediaInfo("video/ogg", ".ogv");
        if (length >= 4 && header[..4].SequenceEqual((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
            return audio ? new MediaInfo("audio/webm", ".webm") : new MediaInfo("video/webm", ".webm");
        if (length >= 12 && header[4..8].SequenceEqual("ftyp"u8))
            return audio ? new MediaInfo("audio/mp4", ".m4a")
                : header[8..12].SequenceEqual("qt  "u8) ? new MediaInfo("video/quicktime", ".mov")
                : new MediaInfo("video/mp4", ".mp4");
        if (!audio) return null;
        if (length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8))
            return new MediaInfo("audio/wav", ".wav");
        if (length >= 4 && header[..4].SequenceEqual("fLaC"u8)) return new MediaInfo("audio/flac", ".flac");
        // MP3: an ID3 tag, or straight into an MPEG audio frame (11 sync bits, layer III).
        if (length >= 3 && header[..3].SequenceEqual("ID3"u8) ||
            length >= 2 && header[0] == 0xFF && (header[1] & 0xE6) == 0xE2)
            return new MediaInfo("audio/mpeg", ".mp3");
        return null;
    }
}
