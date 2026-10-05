using System.Diagnostics;
using Dante.Application.Anexos;
using Dante.Infrastructure.Agentes;
using Dante.Worker.Artifacts;
using Dante.Worker.Attachments;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Xunit.Abstractions;

namespace Dante.Tests;

// Local evidence for #98 with the real tools: synthetic prints made by ffmpeg (phone and desktop sizes, one with
// "MODO NORMAL" and one with "ALTO CONTRASTE" written on it) become showcase PNGs through ShowcaseRenderer; then a
// Claude and a Codex session answer the /vitrine request built by TelegramShowcase, and their spec is rendered.
// The images are kept in $TMPDIR/dante-showcase-evidence for inspection. ffmpeg runs with DANTE_LIVE_MEDIA=1; the
// agents also need DANTE_LIVE_CLI=1, since they consume real quota:
//   DANTE_LIVE_MEDIA=1 [DANTE_LIVE_CLI=1] dotnet test Dante.sln --filter "FullyQualifiedName~LiveShowcaseEvidenceTests"
public sealed class LiveShowcaseEvidenceTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private static readonly string[] PhoneLabels = ["Início", "Menu aberto", "Cadastro", "Alto contraste"];
    private static readonly string Evidence =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dante-showcase-evidence")).FullName;

    [LiveMediaFact]
    public async Task FfmpegRendersPhoneAndDesktopShowcases()
    {
        var phones = await PrintsAsync("phone", 390, 844, 4);
        var phone = new ShowcaseSpec(ShowcaseFormat.Landscape, "No celular também",
            "Menu acessível pelo teclado, formulário com máscaras e alto contraste.", "#1B4D3E",
            phones.Select((print, index) => new ShowcasePrint(print.Id, PhoneLabels[index], index == 3)).ToArray(),
            "projeto.example");
        var desktops = await PrintsAsync("desktop", 1440, 900, 2);
        var desktop = new ShowcaseSpec(ShowcaseFormat.Portrait, "Mesmo site, dois modos de leitura",
            "Um clique no botão \"Alto contraste\" e a preferência fica salva.", "#1B4D3E",
            [new(desktops[0].Id, "Modo normal", false), new(desktops[1].Id, "Alto contraste", true)], "");

        await RenderAsync(phone, phones, "phones.png");
        await RenderAsync(desktop, desktops, "desktops.png");
    }

    [LiveAgentFact]
    public Task ClaudeAnswersTheShowcaseContract() => AgentAsync(AgentKind.Claude);

    [LiveAgentFact]
    public Task CodexAnswersTheShowcaseContract() => AgentAsync(AgentKind.Codex);

    private async Task AgentAsync(AgentKind agent)
    {
        var prints = await PrintsAsync(agent.ToString().ToLowerInvariant(), 1440, 900, 2);
        var attachments = prints.Select(print => new Attachment(print.Id, 1, AttachmentKind.Image, "image/png",
            print.Path, new FileInfo(print.Path).Length, print.Width, print.Height, null)).ToArray();
        var request = TelegramShowcase.RequestText("Monte uma vitrine para o LinkedIn mostrando que o site tem um modo " +
            "de alto contraste; destaque esse modo.", attachments, new Dictionary<string, string?>());

        var launcher = new InteractiveAgentProcessLauncher(new AgentExecutableResolver());
        IAgentSessionDriver driver = agent == AgentKind.Claude
            ? new ClaudeSessionDriver(launcher) : new CodexSessionDriver(launcher);
        await using (driver)
        {
            using var timeout = new CancellationTokenSource(Timeout);
            var workspace = Directory.CreateTempSubdirectory("dante-showcase-workspace-").FullName;
            await driver.StartAsync(new AgentSessionStartOptions(workspace, IsGeneral: true), timeout.Token);
            await driver.StartTurnAsync(new AgentInput(request, attachments), timeout.Token);
            var replies = new List<string>();
            await foreach (var agentEvent in driver.ReadEventsAsync(timeout.Token))
            {
                if (agentEvent is MessageCompletedEvent message) replies.Add(message.Text);
                if (agentEvent is TurnCompletedEvent completed)
                {
                    Assert.Equal(AgentTurnOutcome.Completed, completed.Outcome);
                    break;
                }
            }
            var reply = string.Join('\n', replies);
            output.WriteLine($"{agent}: {reply}");
            var (spec, error) = ShowcaseSpec.Parse(reply, prints.Select(print => print.Id).ToArray());
            Assert.True(spec is not null, error);
            // The agent saw the prints: the highlighted one is the high-contrast print.
            Assert.Equal(prints[1].Id, Assert.Single(spec.Prints, print => print.Highlight).Id);
            await RenderAsync(spec, prints, $"{agent.ToString().ToLowerInvariant()}.png");
        }
    }

    private async Task RenderAsync(ShowcaseSpec spec, IReadOnlyList<ShowcaseImage> prints, string name)
    {
        var path = Path.Combine(Evidence, name);
        await new ShowcaseRenderer().RenderAsync(spec, prints, path, CancellationToken.None);
        var image = ImageInspector.Inspect(path);
        Assert.Equal(("image/png", ShowcaseLayoutBuilder.Canvas(spec.Format)), (image!.MediaType, (image.Width, image.Height)));
        output.WriteLine($"{path}: {image.Width}×{image.Height}, {new FileInfo(path).Length / 1024} KB");
    }

    // Light print with "MODO NORMAL", then dark prints with "ALTO CONTRASTE" in bright green.
    private static async Task<IReadOnlyList<ShowcaseImage>> PrintsAsync(string prefix, int width, int height, int count)
    {
        var prints = new List<ShowcaseImage>();
        for (var index = 0; index < count; index++)
        {
            var (background, color, text) = index == 0 ? ("white", "0x1B4D3E", "MODO NORMAL") : ("black", "0x00FF88", "ALTO CONTRASTE");
            var path = Path.Combine(Evidence, $"{prefix}-print{index + 1}.png");
            await RunAsync("ffmpeg", "-nostdin", "-v", "error", "-f", "lavfi", "-i",
                $"color=c={background}:s={width}x{height}:d=1", "-vf",
                $"drawtext=fontfile=/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf:text={text}:fontcolor={color}:" +
                $"fontsize={width / 12}:x=(w-text_w)/2:y=(h-text_h)/2", "-frames:v", "1", "-y", path);
            prints.Add(new ShowcaseImage($"A00000{index + 1}", path, width, height));
        }
        return prints;
    }

    private static async Task RunAsync(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(Timeout);
        Assert.True(process.ExitCode == 0, $"{program} falhou: {await stderr}");
    }

    private sealed class LiveMediaFactAttribute : FactAttribute
    {
        public LiveMediaFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DANTE_LIVE_MEDIA") != "1")
                Skip = "Evidência com o ffmpeg real; rode com DANTE_LIVE_MEDIA=1.";
        }
    }

    private sealed class LiveAgentFactAttribute : FactAttribute
    {
        public LiveAgentFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DANTE_LIVE_MEDIA") != "1" ||
                Environment.GetEnvironmentVariable("DANTE_LIVE_CLI") != "1")
                Skip = "Evidência com o ffmpeg e as CLIs reais; rode com DANTE_LIVE_MEDIA=1 e DANTE_LIVE_CLI=1.";
        }
    }
}
