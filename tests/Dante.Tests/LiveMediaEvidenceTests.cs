using System.Diagnostics;
using Dante.Worker.Agents;
using Dante.Worker.Attachments;
using Dante.Worker.Sessions;
using Xunit.Abstractions;

namespace Dante.Tests;

// Local evidence for #96 with the real tools: synthetic speech (Windows text-to-speech, from WSL) as a Telegram-like
// OGG/Opus voice, and a synthetic video (red then blue, with a tone) made by ffmpeg, go through MediaTools and
// MediaPreparer; then a Claude and a Codex session receive the prepared turn through MediaPreparingSessionDriver.
// The tools run with DANTE_LIVE_MEDIA=1; the agents also need DANTE_LIVE_CLI=1, since they consume real quota:
//   DANTE_LIVE_MEDIA=1 [DANTE_LIVE_CLI=1] dotnet test Dante.sln --filter "FullyQualifiedName~LiveMediaEvidenceTests"
public sealed class LiveMediaEvidenceTests(ITestOutputHelper output) : IDisposable
{
    // The installed Windows voices are en-US: an English phrase is what the voice can actually say.
    private const string Phrase = "The secret word is sunflower.";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private readonly string root = Directory.CreateTempSubdirectory("dante-live-media-").FullName;

    [LiveMediaFact]
    public async Task SyntheticVoiceIsTranscribedWithTimestamps()
    {
        var prepared = await new MediaPreparer(new MediaTools()).PrepareAsync(
            new AgentInput("Qual é a palavra secreta?", [await VoiceAsync()]), CancellationToken.None);

        output.WriteLine(prepared.Text);
        Assert.Matches("(?i)\\[0:0\\d–0:0\\d\\] .*secret word is sunflower", prepared.Text);
        Assert.Contains("idioma detectado: en", prepared.Text);
        Assert.Equal(["A000001.ogg"], Directory.EnumerateFiles(Scope()).Select(Path.GetFileName));
    }

    [LiveMediaFact]
    public async Task SyntheticVideoBecomesFramesAndAnAudioTrackReading()
    {
        var prepared = await new MediaPreparer(new MediaTools()).PrepareAsync(
            new AgentInput("Descreva o vídeo.", [await VideoAsync()]), CancellationToken.None);

        output.WriteLine(prepared.Text);
        Assert.Equal(MediaPreparer.MaxFramesPerVideo, prepared.Attachments.Count);
        foreach (var frame in prepared.Attachments)
        {
            var image = ImageInspector.Inspect(frame.Path);
            Assert.Equal(("image/jpeg", 320, 240), (image!.MediaType, image.Width, image.Height));
        }
        Assert.Contains("Vídeo 1 (cores.mp4), duração 0:12. 6 quadros amostrados, nas imagens 1 a 6 (em 0:01, 0:03, 0:05, 0:07, " +
            "0:09, 0:11).", prepared.Text);
        Assert.Contains("Trilha de áudio: Transcrição", prepared.Text);
    }

    [LiveAgentFact]
    public async Task ClaudeSessionHearsTheVoice() =>
        Assert.Contains("sunflower", await SessionAsync(AgentKind.Claude, "Qual é a palavra secreta dita no áudio? " +
            "Responda só a palavra, como foi dita.", await VoiceAsync()), StringComparison.OrdinalIgnoreCase);

    [LiveAgentFact]
    public async Task CodexSessionSeesTheVideoFrames() =>
        Assert.Matches("(?is)vermelh.*azul", await SessionAsync(AgentKind.Codex, "Quais cores aparecem nos quadros " +
            "do vídeo, na ordem do tempo? Responda só com os nomes das cores, em português.", await VideoAsync()));

    private async Task<string> SessionAsync(AgentKind agent, string prompt, Attachment media)
    {
        var launcher = new InteractiveAgentProcessLauncher(new AgentExecutableResolver());
        IAgentSessionDriver inner = agent == AgentKind.Claude
            ? new ClaudeSessionDriver(launcher) : new CodexSessionDriver(launcher);
        await using var driver = new MediaPreparingSessionDriver(inner, new MediaPreparer(new MediaTools()));
        using var timeout = new CancellationTokenSource(Timeout);
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        await driver.StartAsync(new AgentSessionStartOptions(workspace, IsGeneral: true), timeout.Token);
        await driver.StartTurnAsync(new AgentInput(prompt, [media]), timeout.Token);
        var text = new List<string>();
        await foreach (var agentEvent in driver.ReadEventsAsync(timeout.Token))
        {
            if (agentEvent is MessageCompletedEvent message) text.Add(message.Text);
            if (agentEvent is TurnCompletedEvent completed)
            {
                Assert.Equal(AgentTurnOutcome.Completed, completed.Outcome);
                break;
            }
        }
        var answer = string.Join('\n', text);
        output.WriteLine($"{agent} sessão: {answer}");
        return answer;
    }

    // Like the attachment store: one scope directory outside the workspace, the original named by the D.A.N.T.E.
    private string Scope() => Directory.CreateDirectory(Path.Combine(root, "attachments", "S000001")).FullName;

    private async Task<Attachment> VoiceAsync()
    {
        var wav = Path.Combine(root, "speech.wav");
        var script = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "spikes", "multimodal",
            "make_speech.ps1");
        await RunAsync("powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            await WindowsPathAsync(Path.GetFullPath(script)), "-OutFile", await WindowsPathAsync(wav), "-Text", Phrase);
        var path = Path.Combine(Scope(), "A000001.ogg");
        // What Telegram sends as a voice message: Opus in OGG.
        await RunAsync("ffmpeg", "-nostdin", "-v", "error", "-i", wav, "-c:a", "libopus", "-b:a", "32k", "-y", path);
        return new Attachment("A000001", 1, AttachmentKind.Audio, "audio/ogg", path, new FileInfo(path).Length, null,
            null, null);
    }

    private async Task<Attachment> VideoAsync()
    {
        var path = Path.Combine(Scope(), "A000002.mp4");
        await RunAsync("ffmpeg", "-nostdin", "-v", "error", "-f", "lavfi", "-i",
            "color=c=red:s=320x240:d=6,format=yuv420p[a];color=c=blue:s=320x240:d=6,format=yuv420p[b];[a][b]concat=n=2:v=1:a=0",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=12", "-shortest", "-c:v", "libx264", "-c:a", "aac", "-y",
            path);
        return new Attachment("A000002", 1, AttachmentKind.Video, "video/mp4", path, new FileInfo(path).Length, null,
            null, "cores.mp4");
    }

    private static async Task<string> WindowsPathAsync(string path) => (await RunAsync("wslpath", "-w", path)).Trim();

    private static async Task<string> RunAsync(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(Timeout);
        Assert.True(process.ExitCode == 0, $"{program} falhou: {await stderr}");
        return await stdout;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class LiveMediaFactAttribute : FactAttribute
    {
        public LiveMediaFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DANTE_LIVE_MEDIA") != "1")
                Skip = "Evidência com ffmpeg e whisper.cpp reais; rode com DANTE_LIVE_MEDIA=1.";
        }
    }

    private sealed class LiveAgentFactAttribute : FactAttribute
    {
        public LiveAgentFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DANTE_LIVE_MEDIA") != "1" ||
                Environment.GetEnvironmentVariable("DANTE_LIVE_CLI") != "1")
                Skip = "Evidência com as ferramentas e as CLIs reais; rode com DANTE_LIVE_MEDIA=1 e DANTE_LIVE_CLI=1.";
        }
    }
}
