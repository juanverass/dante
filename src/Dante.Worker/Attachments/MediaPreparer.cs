using System.Globalization;
using System.Text;
using Dante.Application.Anexos;
using Dante.Worker.Sessions;

namespace Dante.Worker.Attachments;

// Why audio or video could not be prepared, in words the user can act on (missing tool, invalid file, time limit).
public sealed class MediaPreparationException(string message) : Exception(message);

// No CLI hears audio or watches video (#93 spike), so before a turn or one-shot reaches the agent the D.A.N.T.E.
// turns them into what the agent can receive (#96): speech becomes a local whisper.cpp transcript with timestamps,
// and a video becomes a few sampled frames (sent as images) plus the transcript of its audio. The text says where
// each piece came from and what was not analysed. Originals stay untouched in the session or job directory; the
// frames live there too and go away with it, intermediate files are removed right away.
public sealed class MediaPreparer(IMediaTools tools, TimeSpan? timeout = null)
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(10);
    public const int MaxFramesPerVideo = 6;
    // Images per turn stay within the AD-29 limit, counting the frames.
    public const int MaxImages = 10;
    public const int MaxTranscriptLength = 30_000;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
    private readonly TimeSpan timeout = timeout ?? DefaultTimeout;

    public static bool NeedsPreparation(IReadOnlyList<Attachment> attachments) =>
        attachments.Any(attachment => attachment.Kind is AttachmentKind.Audio or AttachmentKind.Video);

    // Checked when a file arrives, before downloading it, and again before preparing it.
    public string? Unavailable(AttachmentKind kind) => tools.Unavailable(kind);

    // A short description for progress lines: "1 áudio", "2 vídeos", "1 áudio e 1 vídeo".
    public static string Describe(IReadOnlyList<Attachment> attachments)
    {
        var audio = attachments.Count(attachment => attachment.Kind == AttachmentKind.Audio);
        var video = attachments.Count(attachment => attachment.Kind == AttachmentKind.Video);
        return string.Join(" e ", new[]
        {
            audio == 0 ? null : $"{audio} {(audio == 1 ? "áudio" : "áudios")}",
            video == 0 ? null : $"{video} {(video == 1 ? "vídeo" : "vídeos")}"
        }.OfType<string>());
    }

    // The input the agent receives: the user's text followed by the derived content, and the images in order, each
    // video replaced by its frames. Cancellation of the turn or job stops the tools; a timeout or failure throws a
    // MediaPreparationException. Either way nothing derived is left behind.
    public async Task<AgentInput> PrepareAsync(AgentInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!NeedsPreparation(input.Attachments)) return input;
        foreach (var kind in input.Attachments.Select(attachment => attachment.Kind).Distinct())
            if (kind is AttachmentKind.Audio or AttachmentKind.Video && tools.Unavailable(kind) is { } missing)
                throw new MediaPreparationException($"Não processei {Article(kind)}: {missing}.");

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var frames = new List<string>();
        try
        {
            return await PrepareAsync(input, frames, limit.Token);
        }
        catch (Exception exception)
        {
            foreach (var frame in frames) DeleteQuietly(frame);
            if (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new MediaPreparationException($"O processamento de áudio/vídeo passou de " +
                    $"{(timeout.TotalMinutes >= 1 ? $"{timeout.TotalMinutes:0} min" : $"{timeout.TotalSeconds:0} s")} " +
                    "e foi cancelado.");
            throw;
        }
    }

    private async Task<AgentInput> PrepareAsync(AgentInput input, List<string> frames, CancellationToken token)
    {
        var budget = MaxImages - input.Attachments.Count(attachment => attachment.Kind == AttachmentKind.Image);
        var images = new List<Attachment>();
        var text = new StringBuilder();
        var (audioNumber, videoNumber) = (0, 0);
        foreach (var attachment in input.Attachments)
        {
            switch (attachment.Kind)
            {
                case AttachmentKind.Image:
                    images.Add(attachment);
                    break;
                case AttachmentKind.Audio:
                    text.Append('\n').Append(await AudioAsync(attachment, ++audioNumber, token));
                    break;
                case AttachmentKind.Video:
                    var (description, sampled) = await VideoAsync(attachment, ++videoNumber, images.Count,
                        Math.Max(0, Math.Min(MaxFramesPerVideo, budget)), frames, token);
                    budget -= sampled.Count;
                    images.AddRange(sampled);
                    text.Append('\n').Append(description);
                    break;
                default:
                    throw new MediaPreparationException("Só imagens, áudio e vídeo chegam ao agente.");
            }
        }

        return new AgentInput(input.Text + "\n\n[Anexos processados localmente pelo D.A.N.T.E.: o agente não recebe " +
            "os arquivos de áudio ou vídeo, só o conteúdo derivado abaixo. Transcrições são automáticas e podem " +
            "conter erros.]" + text, images);
    }

    private async Task<string> AudioAsync(Attachment audio, int number, CancellationToken token)
    {
        var probe = await ProbeAsync(audio, token);
        if (!probe.HasAudio)
            throw new MediaPreparationException($"O áudio {number} não tem trilha de áudio legível.");
        var header = new StringBuilder($"Áudio {number}{Name(audio)}, duração {Length(probe)}");
        if (probe.Duration > MaxDuration)
            header.Append($"; análise parcial: só os primeiros {Clock(MaxDuration)} foram transcritos");
        return header + ". " + await TranscribeAsync(audio, Min(probe.Duration, MaxDuration), token);
    }

    private async Task<(string Description, IReadOnlyList<Attachment> Frames)> VideoAsync(Attachment video,
        int number, int imagesBefore, int budget, List<string> created, CancellationToken token)
    {
        var probe = await ProbeAsync(video, token);
        if (!probe.HasVideo && !probe.HasAudio)
            throw new MediaPreparationException($"O vídeo {number} não tem imagem nem som legíveis.");
        var analysed = Min(probe.Duration, MaxDuration);
        var description = new StringBuilder($"Vídeo {number}{Name(video)}, duração {Length(probe)}");
        if (probe.Duration > MaxDuration)
            description.Append($"; análise parcial: só os primeiros {Clock(MaxDuration)} foram amostrados e transcritos");
        description.Append(". ");

        var frames = new List<Attachment>();
        if (!probe.HasVideo) description.Append("Sem imagem: nenhum quadro foi extraído. ");
        else if (budget == 0)
            description.Append($"Nenhum quadro enviado: limite de {MaxImages} imagens por mensagem atingido. ");
        else
        {
            // Evenly spaced, each in the middle of its slice; a very short video gets a single frame.
            var count = analysed > TimeSpan.Zero ? Math.Min(budget, Math.Max(1, (int)analysed.TotalSeconds)) : 1;
            var times = Enumerable.Range(0, count).Select(index => analysed * ((index + 0.5) / count)).ToArray();
            foreach (var (at, index) in times.Select((at, index) => (at, index)))
            {
                var path = Path.Combine(Path.GetDirectoryName(video.Path)!, $"{video.Id}-Q{index + 1}.jpg");
                created.Add(path);
                try { await tools.ExtractFrameAsync(video.Path, at, path, token); }
                catch (MediaToolException exception)
                {
                    throw new MediaPreparationException($"Não consegui extrair quadros do vídeo {number}: " +
                        $"{exception.Message}.");
                }
                var image = File.Exists(path) ? ImageInspector.Inspect(path) : null;
                if (image is null)
                    throw new MediaPreparationException($"Não consegui extrair quadros do vídeo {number}.");
                frames.Add(new Attachment($"{video.Id}-Q{index + 1}", video.OwnerId, AttachmentKind.Image,
                    image.MediaType, path, new FileInfo(path).Length, image.Width, image.Height,
                    $"vídeo {number}, quadro em {Clock(at)}"));
            }
            var first = imagesBefore + 1;
            description.Append(frames.Count == 1
                ? $"1 quadro amostrado, na imagem {first} (em {Clock(times[0])}). "
                : $"{frames.Count} quadros amostrados, nas imagens {first} a {first + frames.Count - 1} (em " +
                  $"{string.Join(", ", times.Select(Clock))}). ");
            description.Append("O que acontece entre os quadros não foi visto. ");
        }

        if (!probe.HasAudio) description.Append("Sem trilha de áudio.");
        else if (tools.Unavailable(AttachmentKind.Audio) is { } missing)
            description.Append($"Trilha de áudio não transcrita: {missing}.");
        else description.Append("Trilha de áudio: ").Append(await TranscribeAsync(video, analysed, token));
        return (description.ToString(), frames);
    }

    private async Task<MediaProbe> ProbeAsync(Attachment attachment, CancellationToken token)
    {
        try { return await tools.ProbeAsync(attachment.Path, token); }
        catch (MediaToolException)
        {
            throw new MediaPreparationException($"Não consegui ler {Article(attachment.Kind)}{Name(attachment)}: " +
                "arquivo inválido ou corrompido.");
        }
    }

    // The intermediate WAV and whisper's JSON never outlive the transcription.
    private async Task<string> TranscribeAsync(Attachment source, TimeSpan duration, CancellationToken token)
    {
        var wav = Path.Combine(Path.GetDirectoryName(source.Path)!, $"{source.Id}-audio.wav");
        Transcript transcript;
        try
        {
            await tools.ExtractAudioAsync(source.Path, wav, duration > TimeSpan.Zero ? duration : MaxDuration, token);
            transcript = await tools.TranscribeAsync(wav, token);
        }
        catch (MediaToolException exception)
        {
            throw new MediaPreparationException($"Não consegui transcrever {Article(source.Kind)}{Name(source)}: " +
                $"{exception.Message}.");
        }
        finally
        {
            DeleteQuietly(wav);
        }

        var segments = transcript.Segments.Where(segment => segment.Text.Length > 0 &&
            segment.Text != "[BLANK_AUDIO]").ToArray();
        if (segments.Length == 0) return "Transcrição: nenhuma fala reconhecida.";
        var text = new StringBuilder("Transcrição (whisper.cpp" +
            (transcript.Language is { Length: > 0 } language ? $", idioma detectado: {language}" : "") + "):");
        foreach (var segment in segments)
        {
            var line = $"\n[{Clock(segment.From)}–{Clock(segment.To)}] {segment.Text}";
            if (text.Length + line.Length > MaxTranscriptLength)
            {
                text.Append($"\n[transcrição truncada em {MaxTranscriptLength} caracteres; o restante não foi enviado]");
                break;
            }
            text.Append(line);
        }
        return text.ToString();
    }

    private static string Article(AttachmentKind kind) => kind == AttachmentKind.Video ? "o vídeo" : "o áudio";

    private static string Name(Attachment attachment) => attachment.Name is { } name ? $" ({name})" : "";

    private static string Length(MediaProbe probe) =>
        probe.Duration > TimeSpan.Zero ? Clock(probe.Duration) : "desconhecida";

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string Clock(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static void DeleteQuietly(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
