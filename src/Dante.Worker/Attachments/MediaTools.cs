using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dante.Application.Anexos;

namespace Dante.Worker.Attachments;

public sealed record MediaProbe(TimeSpan Duration, bool HasAudio, bool HasVideo);

public sealed record TranscriptSegment(TimeSpan From, TimeSpan To, string Text);

public sealed record Transcript(string? Language, IReadOnlyList<TranscriptSegment> Segments);

// A tool that is missing, refused the file or failed. The message is shown to the user: no path, no tool output.
public sealed class MediaToolException(string message) : Exception(message);

// The local programs behind audio and video (#96): ffprobe and ffmpeg read the file, whisper.cpp transcribes speech
// with a local model. Nothing is installed or contracted here (AD-29): a missing piece is reported before running.
public interface IMediaTools
{
    // Null when everything the kind needs is installed; otherwise what is missing and how to install it.
    string? Unavailable(AttachmentKind kind);

    Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken);

    // The first audio stream, mono 16 kHz WAV (what whisper.cpp reads), cut at limit.
    Task ExtractAudioAsync(string path, string wavPath, TimeSpan limit, CancellationToken cancellationToken);

    // One frame of the first video stream at the given time, as a JPEG at most 1280 px wide.
    Task ExtractFrameAsync(string path, TimeSpan at, string jpegPath, CancellationToken cancellationToken);

    Task<Transcript> TranscribeAsync(string wavPath, CancellationToken cancellationToken);
}

// Fixed program names resolved in absolute PATH entries, ArgumentList, no shell and a minimal environment (AD-03).
// Inputs are only files of the attachment store; ffmpeg may open nothing but local files.
public sealed class MediaTools(string? modelPath = null) : IMediaTools
{
    private const int MaxOutputLength = 1024 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public string ModelPath { get; } = modelPath ?? DefaultModelPath();

    private static string DefaultModelPath() =>
        Environment.GetEnvironmentVariable("DANTE_WHISPER_MODEL") is { Length: > 0 } configured ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dante", "models",
                "ggml-small.bin");

    public string? Unavailable(AttachmentKind kind)
    {
        foreach (var tool in new[] { "ffprobe", "ffmpeg" })
            if (Resolve(tool) is null)
                return $"{tool} não encontrado no PATH; instale o ffmpeg (sudo apt install ffmpeg)";
        if (kind != AttachmentKind.Audio) return null;
        if (Resolve("whisper-cli") is null)
            return "whisper-cli não encontrado no PATH; instale o whisper.cpp (sudo apt install whisper.cpp)";
        if (!Path.IsPathFullyQualified(ModelPath) || !File.Exists(ModelPath))
            return "modelo de transcrição ausente; baixe o ggml-small.bin do whisper.cpp para ~/.dante/models/ ou " +
                "defina DANTE_WHISPER_MODEL com o caminho absoluto do modelo";
        return null;
    }

    public async Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        string output;
        try
        {
            output = await RunAsync("ffprobe", ["-v", "error", "-protocol_whitelist", "file",
                "-show_entries", "format=duration:stream=codec_type", "-of", "json", path], timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MediaToolException("ffprobe não respondeu a tempo");
        }

        try
        {
            using var json = JsonDocument.Parse(output);
            var types = json.RootElement.TryGetProperty("streams", out var streams)
                ? streams.EnumerateArray().Select(stream => stream.TryGetProperty("codec_type", out var type)
                    ? type.GetString() : null).ToArray()
                : [];
            var duration = json.RootElement.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var value) &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                double.IsFinite(seconds) && seconds > 0
                    ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
            return new MediaProbe(duration, types.Contains("audio"), types.Contains("video"));
        }
        catch (JsonException)
        {
            throw new MediaToolException("ffprobe devolveu uma resposta inesperada");
        }
    }

    public Task ExtractAudioAsync(string path, string wavPath, TimeSpan limit, CancellationToken cancellationToken) =>
        RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-protocol_whitelist", "file", "-i", path, "-map", "0:a:0",
            "-vn", "-ac", "1", "-ar", "16000", "-t", Seconds(limit), "-c:a", "pcm_s16le", "-f", "wav", "-y", wavPath],
            cancellationToken);

    public Task ExtractFrameAsync(string path, TimeSpan at, string jpegPath, CancellationToken cancellationToken) =>
        RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-protocol_whitelist", "file", "-ss", Seconds(at), "-i", path,
            "-map", "0:v:0", "-frames:v", "1", "-vf", "scale='min(1280,iw)':-2", "-q:v", "3", "-f", "image2",
            "-y", jpegPath], cancellationToken);

    public async Task<Transcript> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        // whisper.cpp writes <base>.json next to the audio; it is read and removed here.
        var output = Path.ChangeExtension(wavPath, null);
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8).ToString(CultureInfo.InvariantCulture);
        try
        {
            await RunAsync("whisper-cli", ["-m", ModelPath, "-f", wavPath, "-l", "auto", "-t", threads, "-np",
                "-oj", "-of", output], cancellationToken);
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(output + ".json", cancellationToken));
            var language = json.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("language", out var detected) ? detected.GetString() : null;
            var segments = new List<TranscriptSegment>();
            if (json.RootElement.TryGetProperty("transcription", out var transcription))
                foreach (var segment in transcription.EnumerateArray())
                {
                    var text = segment.GetProperty("text").GetString()?.Trim() ?? "";
                    var offsets = segment.GetProperty("offsets");
                    segments.Add(new TranscriptSegment(TimeSpan.FromMilliseconds(offsets.GetProperty("from").GetInt64()),
                        TimeSpan.FromMilliseconds(offsets.GetProperty("to").GetInt64()), text));
                }
            return new Transcript(language, segments);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException
                                              or FormatException or IOException)
        {
            throw new MediaToolException("whisper-cli não produziu uma transcrição legível");
        }
        finally
        {
            File.Delete(output + ".json");
        }
    }

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static async Task<string> RunAsync(string tool, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var executable = Resolve(tool) ?? throw new MediaToolException($"{tool} não encontrado no PATH");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };
        var inherited = new Dictionary<string, string?>(start.Environment, StringComparer.Ordinal);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "HOME", "LANG", "LC_ALL", "TMPDIR" })
            if (inherited.TryGetValue(name, out var value) && value is not null)
                start.Environment[name] = value;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new MediaToolException($"{tool} não pôde ser iniciado");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new MediaToolException($"{tool} não pôde ser iniciado");
        }
        process.StandardInput.Close();
        var stdout = ReadLimitedAsync(process.StandardOutput);
        var stderr = ReadLimitedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Already exited. */ }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var output = await stdout;
        await stderr;
        return process.ExitCode == 0 ? output
            : throw new MediaToolException($"{tool} terminou com código {process.ExitCode}");
    }

    // Both streams are drained so the tool never blocks on a full pipe; only the first megabyte is kept.
    private static async Task<string> ReadLimitedAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
            if (text.Length < MaxOutputLength) text.Append(buffer, 0, Math.Min(read, MaxOutputLength - text.Length));
        return text.ToString();
    }

    private static string? Resolve(string tool)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // An empty or relative PATH entry would make the current directory executable.
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? tool + ".exe" : tool);
            if (File.Exists(candidate) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(candidate) &
                    (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
                return candidate;
        }
        return null;
    }
}
