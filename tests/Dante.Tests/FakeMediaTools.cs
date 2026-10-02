using System.Collections.Concurrent;
using Dante.Worker.Attachments;

namespace Dante.Tests;

// Media tools without ffmpeg or whisper.cpp: probes are configured per file, frames are tiny JPEGs, the WAV is a stub
// and the transcript is whatever the test sets. Every call is recorded; Gate holds the tools until the test opens it.
internal sealed class FakeMediaTools : IMediaTools
{
    private readonly ConcurrentQueue<string> calls = new();

    public Dictionary<AttachmentKind, string> Missing { get; } = [];
    public Dictionary<string, MediaProbe> Probes { get; } = new(StringComparer.Ordinal);
    public MediaProbe DefaultProbe { get; set; } = new(TimeSpan.FromSeconds(42), HasAudio: true, HasVideo: false);
    public Transcript Transcript { get; set; } = new("pt",
        [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(4.5), "A palavra secreta é girassol.")]);
    public bool ProbeFails { get; set; }
    public bool FrameFails { get; set; }
    public TaskCompletionSource? Gate { get; set; }
    // Called when the transcription starts; the returned task holds it (until cancelled, if it never completes).
    public Func<Task>? Transcribing { get; set; }
    public IReadOnlyList<string> Calls => calls.ToArray();
    public List<string> WavFiles { get; } = [];

    public string? Unavailable(AttachmentKind kind) => Missing.GetValueOrDefault(kind);

    public async Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        calls.Enqueue("probe:" + Path.GetFileName(path));
        await WaitAsync(cancellationToken);
        if (ProbeFails) throw new MediaToolException("ffprobe terminou com código 1");
        return Probes.GetValueOrDefault(Path.GetFileName(path)) ?? DefaultProbe;
    }

    public async Task ExtractAudioAsync(string path, string wavPath, TimeSpan limit, CancellationToken cancellationToken)
    {
        calls.Enqueue($"audio:{Path.GetFileName(path)}:{limit.TotalSeconds}");
        await File.WriteAllBytesAsync(wavPath, [1, 2, 3], cancellationToken);
        lock (WavFiles) WavFiles.Add(wavPath);
        await WaitAsync(cancellationToken);
    }

    public async Task ExtractFrameAsync(string path, TimeSpan at, string jpegPath, CancellationToken cancellationToken)
    {
        calls.Enqueue($"frame:{Path.GetFileName(path)}:{at.TotalSeconds}");
        await WaitAsync(cancellationToken);
        if (FrameFails) throw new MediaToolException("ffmpeg terminou com código 1");
        await File.WriteAllBytesAsync(jpegPath, TestImages.Jpeg(64, 36), cancellationToken);
    }

    public async Task<Transcript> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        calls.Enqueue("transcribe:" + Path.GetFileName(wavPath));
        await WaitAsync(cancellationToken);
        if (Transcribing is { } hold) await hold().WaitAsync(cancellationToken);
        return Transcript;
    }

    private Task WaitAsync(CancellationToken cancellationToken) =>
        Gate is { } gate ? gate.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
}

// Minimal containers: enough for MediaInspector, which identifies audio and video by their first bytes.
internal static class TestMedia
{
    public static byte[] Ogg() => [.. "OggS"u8, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public static byte[] Mp4(string brand = "isom") =>
        [0, 0, 0, 0x18, .. "ftyp"u8, .. System.Text.Encoding.ASCII.GetBytes(brand), 0, 0, 2, 0];

    public static byte[] Mp3() => [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 0];
}
