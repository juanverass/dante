using Dante.Application.Anexos;
using Dante.Worker.Attachments;
using Dante.Worker.Sessions;

namespace Dante.Tests;

// Audio and video become what the agents receive (#96): transcript with timestamps, sampled frames as images, the
// provenance and every part that was not analysed, within limits, and nothing derived left behind on failure.
public sealed class MediaPreparerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dante-prepare-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMediaTools tools = new();

    public MediaPreparerTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task VoiceBecomesATimestampedTranscriptWithItsProvenance()
    {
        var voice = Media("A000001", AttachmentKind.Audio, ".ogg");

        var prepared = await new MediaPreparer(tools).PrepareAsync(new AgentInput("o que eu disse?", [voice]),
            CancellationToken.None);

        Assert.Empty(prepared.Attachments);
        Assert.StartsWith("o que eu disse?\n\n[Anexos processados localmente pelo D.A.N.T.E.", prepared.Text);
        Assert.Contains("Áudio 1, duração 0:42. Transcrição (whisper.cpp, idioma detectado: pt):\n" +
            "[0:00–0:04] A palavra secreta é girassol.", prepared.Text);
        Assert.Equal(["probe:A000001.ogg", "audio:A000001.ogg:42", "transcribe:A000001-audio.wav"], tools.Calls);
        // The original stays for the session; the intermediate WAV does not.
        Assert.True(File.Exists(voice.Path));
        Assert.False(File.Exists(Path.Combine(directory, "A000001-audio.wav")));
    }

    [Fact]
    public async Task VideoIsReplacedByItsFramesInOrderAndItsAudioTranscribed()
    {
        var image = Image("A000001");
        var video = Media("A000002", AttachmentKind.Video, ".mp4", "demo.mp4");
        tools.DefaultProbe = new MediaProbe(TimeSpan.FromSeconds(60), HasAudio: true, HasVideo: true);

        var prepared = await new MediaPreparer(tools).PrepareAsync(new AgentInput("resuma", [image, video]),
            CancellationToken.None);

        Assert.Equal(["A000001", "A000002-Q1", "A000002-Q2", "A000002-Q3", "A000002-Q4", "A000002-Q5", "A000002-Q6"],
            prepared.Attachments.Select(attachment => attachment.Id));
        var frame = prepared.Attachments[1];
        Assert.Equal((AttachmentKind.Image, "image/jpeg", 64, 36), (frame.Kind, frame.MediaType, frame.Width!.Value, frame.Height!.Value));
        Assert.Equal("vídeo 1, quadro em 0:05", frame.Name);
        Assert.Equal(Path.Combine(directory, "A000002-Q1.jpg"), frame.Path);
        Assert.Contains("Vídeo 1 (demo.mp4), duração 1:00. 6 quadros amostrados, nas imagens 2 a 7 (em 0:05, 0:15, " +
            "0:25, 0:35, 0:45, 0:55). O que acontece entre os quadros não foi visto. Trilha de áudio: Transcrição " +
            "(whisper.cpp, idioma detectado: pt):\n[0:00–0:04] A palavra secreta é girassol.", prepared.Text);
    }

    [Fact]
    public async Task LongMediaIsCutAtTheLimitAndDeclaredPartial()
    {
        var voice = Media("A000001", AttachmentKind.Audio, ".ogg");
        tools.DefaultProbe = new MediaProbe(TimeSpan.FromMinutes(25), HasAudio: true, HasVideo: false);

        var prepared = await new MediaPreparer(tools).PrepareAsync(new AgentInput("ouça", [voice]), CancellationToken.None);

        Assert.Contains("Áudio 1, duração 25:00; análise parcial: só os primeiros 10:00 foram transcritos.", prepared.Text);
        Assert.Contains("audio:A000001.ogg:600", tools.Calls);
    }

    [Fact]
    public async Task FramesStayWithinTheImageLimitOfTheTurnAndMissingPartsAreSaid()
    {
        var images = Enumerable.Range(1, 9).Select(index => Image($"A00000{index}")).ToList();
        var first = Media("A000010", AttachmentKind.Video, ".mp4");
        var second = Media("A000011", AttachmentKind.Video, ".mp4");
        tools.Probes["A000010.mp4"] = new MediaProbe(TimeSpan.FromSeconds(10), HasAudio: false, HasVideo: true);
        tools.Probes["A000011.mp4"] = new MediaProbe(TimeSpan.FromSeconds(10), HasAudio: true, HasVideo: true);
        tools.Missing[AttachmentKind.Audio] = "whisper-cli não encontrado no PATH";

        var prepared = await new MediaPreparer(tools).PrepareAsync(new AgentInput("veja", [.. images, first, second]),
            CancellationToken.None);

        Assert.Equal(MediaPreparer.MaxImages, prepared.Attachments.Count);
        Assert.Contains("Vídeo 1, duração 0:10. 1 quadro amostrado, na imagem 10 (em 0:05). O que acontece entre os " +
            "quadros não foi visto. Sem trilha de áudio.", prepared.Text);
        Assert.Contains("Vídeo 2, duração 0:10. Nenhum quadro enviado: limite de 10 imagens por mensagem atingido. " +
            "Trilha de áudio não transcrita: whisper-cli não encontrado no PATH.", prepared.Text);
        Assert.DoesNotContain(tools.Calls, call => call.StartsWith("transcribe:"));
    }

    [Fact]
    public async Task SilenceIsReportedAsNoSpeech()
    {
        tools.Transcript = new Transcript("en", [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(3), "[BLANK_AUDIO]")]);

        var prepared = await new MediaPreparer(tools).PrepareAsync(
            new AgentInput("ouça", [Media("A000001", AttachmentKind.Audio, ".ogg")]), CancellationToken.None);

        Assert.Contains("Transcrição: nenhuma fala reconhecida.", prepared.Text);
    }

    [Fact]
    public async Task AMissingToolIsReportedBeforeRunningAnything()
    {
        tools.Missing[AttachmentKind.Audio] = "whisper-cli não encontrado no PATH; instale o whisper.cpp";

        var error = await Assert.ThrowsAsync<MediaPreparationException>(() => new MediaPreparer(tools).PrepareAsync(
            new AgentInput("ouça", [Media("A000001", AttachmentKind.Audio, ".ogg")]), CancellationToken.None));

        Assert.Equal("Não processei o áudio: whisper-cli não encontrado no PATH; instale o whisper.cpp.", error.Message);
        Assert.Empty(tools.Calls);
    }

    [Fact]
    public async Task InvalidMediaIsReportedWithoutDetailsOfTheTool()
    {
        tools.ProbeFails = true;

        var error = await Assert.ThrowsAsync<MediaPreparationException>(() => new MediaPreparer(tools).PrepareAsync(
            new AgentInput("veja", [Media("A000001", AttachmentKind.Video, ".mp4", "x.mp4")]), CancellationToken.None));

        Assert.Equal("Não consegui ler o vídeo (x.mp4): arquivo inválido ou corrompido.", error.Message);
    }

    [Fact]
    public async Task AVideoWithoutPictureOrSoundIsInvalid()
    {
        tools.DefaultProbe = new MediaProbe(TimeSpan.FromSeconds(5), HasAudio: false, HasVideo: false);

        var error = await Assert.ThrowsAsync<MediaPreparationException>(() => new MediaPreparer(tools).PrepareAsync(
            new AgentInput("veja", [Media("A000001", AttachmentKind.Video, ".mp4")]), CancellationToken.None));

        Assert.Equal("O vídeo 1 não tem imagem nem som legíveis.", error.Message);
    }

    [Fact]
    public async Task TimeoutStopsTheToolsAndRemovesWhatWasDerived()
    {
        tools.DefaultProbe = new MediaProbe(TimeSpan.FromSeconds(20), HasAudio: true, HasVideo: true);
        var preparer = new MediaPreparer(tools, TimeSpan.FromSeconds(1));
        var video = Media("A000001", AttachmentKind.Video, ".mp4");
        // Frames are extracted, then the transcription never returns.
        var reached = false;
        tools.Transcribing = () =>
        {
            reached = true;
            return new TaskCompletionSource().Task;
        };

        var error = await Assert.ThrowsAsync<MediaPreparationException>(() =>
            preparer.PrepareAsync(new AgentInput("veja", [video]), CancellationToken.None));

        Assert.Equal("O processamento de áudio/vídeo passou de 1 s e foi cancelado.", error.Message);
        Assert.True(reached);
        Assert.Equal(["A000001.mp4"], Directory.EnumerateFiles(directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task CancellationPropagatesAndRemovesWhatWasDerived()
    {
        tools.DefaultProbe = new MediaProbe(TimeSpan.FromSeconds(20), HasAudio: true, HasVideo: true);
        using var cancellation = new CancellationTokenSource();
        var reached = false;
        tools.Transcribing = () =>
        {
            reached = true;
            cancellation.Cancel();
            return new TaskCompletionSource().Task;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MediaPreparer(tools).PrepareAsync(
            new AgentInput("veja", [Media("A000001", AttachmentKind.Video, ".mp4")]), cancellation.Token));

        Assert.True(reached);
        Assert.Equal(["A000001.mp4"], Directory.EnumerateFiles(directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AFrameThatCannotBeExtractedFailsThePreparation()
    {
        tools.DefaultProbe = new MediaProbe(TimeSpan.FromSeconds(20), HasAudio: false, HasVideo: true);
        tools.FrameFails = true;

        var error = await Assert.ThrowsAsync<MediaPreparationException>(() => new MediaPreparer(tools).PrepareAsync(
            new AgentInput("veja", [Media("A000001", AttachmentKind.Video, ".mp4")]), CancellationToken.None));

        Assert.Equal("Não consegui extrair quadros do vídeo 1: ffmpeg terminou com código 1.", error.Message);
        Assert.Equal(["A000001.mp4"], Directory.EnumerateFiles(directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task InputsWithoutAudioOrVideoAreUntouched()
    {
        var input = new AgentInput("veja", [Image("A000001")]);

        Assert.Same(input, await new MediaPreparer(tools).PrepareAsync(input, CancellationToken.None));
        Assert.Empty(tools.Calls);
    }

    [Theory]
    [InlineData("ogg", AttachmentKind.Audio, "audio/ogg", ".ogg")]
    [InlineData("ogg", AttachmentKind.Video, "video/ogg", ".ogv")]
    [InlineData("mp4", AttachmentKind.Audio, "audio/mp4", ".m4a")]
    [InlineData("mp4", AttachmentKind.Video, "video/mp4", ".mp4")]
    [InlineData("mov", AttachmentKind.Video, "video/quicktime", ".mov")]
    [InlineData("mp3", AttachmentKind.Audio, "audio/mpeg", ".mp3")]
    [InlineData("webm", AttachmentKind.Video, "video/webm", ".webm")]
    [InlineData("wav", AttachmentKind.Audio, "audio/wav", ".wav")]
    public void ContainersAreIdentifiedByContent(string format, AttachmentKind kind, string mediaType, string extension)
    {
        var path = Path.Combine(directory, "file");
        File.WriteAllBytes(path, format switch
        {
            "ogg" => TestMedia.Ogg(),
            "mp4" => TestMedia.Mp4(),
            "mov" => TestMedia.Mp4("qt  "),
            "mp3" => TestMedia.Mp3(),
            "webm" => [0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0, 0, 0, 0, 0],
            _ => [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8]
        });

        Assert.Equal(new MediaInfo(mediaType, extension), MediaInspector.Inspect(path, kind));
    }

    [Theory]
    [InlineData("#EXTM3U\n#EXT-X-TARGETDURATION:10\nhttp://example.invalid/a.ts\n", AttachmentKind.Video)]
    [InlineData("ID3 is only an audio tag", AttachmentKind.Video)]
    [InlineData("not audio at all", AttachmentKind.Audio)]
    public void PlaylistsTextAndMismatchedKindsAreRefused(string content, AttachmentKind kind)
    {
        var path = Path.Combine(directory, "file");
        File.WriteAllText(path, content);

        Assert.Null(MediaInspector.Inspect(path, kind));
    }

    [Fact]
    public void ProgressDescriptionCountsAudioAndVideo()
    {
        Assert.Equal("2 áudios e 1 vídeo", MediaPreparer.Describe([
            Media("A000001", AttachmentKind.Audio, ".ogg"), Image("A000002"),
            Media("A000003", AttachmentKind.Audio, ".mp3"), Media("A000004", AttachmentKind.Video, ".mp4")]));
    }

    private Attachment Media(string id, AttachmentKind kind, string extension, string? name = null)
    {
        var path = Path.Combine(directory, id + extension);
        File.WriteAllBytes(path, kind == AttachmentKind.Audio ? TestMedia.Ogg() : TestMedia.Mp4());
        return new Attachment(id, 123, kind, kind == AttachmentKind.Audio ? "audio/ogg" : "video/mp4", path,
            new FileInfo(path).Length, null, null, name);
    }

    private static Attachment Image(string id) =>
        new(id, 123, AttachmentKind.Image, "image/png", "/nao/existe/" + id + ".png", 10, 10, 10, null);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
