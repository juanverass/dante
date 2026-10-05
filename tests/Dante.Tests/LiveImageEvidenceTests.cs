using System.Buffers.Binary;
using System.IO.Compression;
using Dante.Application.Agentes;
using Dante.Application.Anexos;
using Dante.Infrastructure.Agentes;
using Dante.Worker.Sessions;
using Xunit.Abstractions;

namespace Dante.Tests;

// Local evidence for #95 with the real CLIs: a synthetic image (left half red, right half blue) goes through the
// drivers and runners of this implementation, outside the workspace and in General Mode, in the four supported paths.
// It consumes real quota, so it only runs on request:
//   DANTE_LIVE_CLI=1 dotnet test Dante.sln --filter "FullyQualifiedName~LiveImageEvidenceTests"
public sealed class LiveImageEvidenceTests(ITestOutputHelper output) : IDisposable
{
    private const string SessionPrompt =
        "Quais cores aparecem na imagem anexada, da esquerda para a direita? Responda só com os nomes, em português.";
    private const string OneShotPrompt =
        "Abra a imagem anexada e diga quais cores aparecem nela, da esquerda para a direita. Responda só com os nomes, " +
        "em português.";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(4);
    private readonly string root = Directory.CreateTempSubdirectory("dante-live-images-").FullName;

    [LiveCliFact]
    public async Task ClaudeSessionSeesTheImage() => Assert.Matches(Colors, await SessionAsync(AgentKind.Claude));

    [LiveCliFact]
    public async Task CodexSessionSeesTheImage() => Assert.Matches(Colors, await SessionAsync(AgentKind.Codex));

    [LiveCliFact]
    public async Task ClaudeOneShotSeesTheImage() => Assert.Matches(Colors, await OneShotAsync(AgentKind.Claude));

    [LiveCliFact]
    public async Task CodexOneShotSeesTheImage() => Assert.Matches(Colors, await OneShotAsync(AgentKind.Codex));

    // Both colors, in the order they appear.
    private static System.Text.RegularExpressions.Regex Colors { get; } =
        new("(?is)vermelh.*azul");

    private async Task<string> SessionAsync(AgentKind agent)
    {
        IAgentSessionDriver driver = agent == AgentKind.Claude
            ? new ClaudeSessionDriver(new InteractiveAgentProcessLauncher(new AgentExecutableResolver()))
            : new CodexSessionDriver(new InteractiveAgentProcessLauncher(new AgentExecutableResolver()));
        await using (driver)
        {
            using var timeout = new CancellationTokenSource(Timeout);
            await driver.StartAsync(new AgentSessionStartOptions(Workspace(), IsGeneral: true), timeout.Token);
            await driver.StartTurnAsync(new AgentInput(SessionPrompt, [Image()]), timeout.Token);
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
    }

    private async Task<string> OneShotAsync(AgentKind agent)
    {
        var executor = new AgentProcessExecutor(new AgentExecutableResolver());
        using var timeout = new CancellationTokenSource(Timeout);
        var result = agent == AgentKind.Claude
            ? await new ClaudeRunner(executor).RunAsync(OneShotPrompt, Workspace(), timeout.Token, generalMode: true,
                attachments: [Image()])
            : await new CodexRunner(executor).RunAsync(OneShotPrompt, Workspace(), timeout.Token, generalMode: true,
                attachments: [Image()]);
        output.WriteLine($"{agent} one-shot ({result.Status}): {result.StandardOutput}");
        Assert.Equal(AgentProcessStatus.Succeeded, result.Status);
        return result.StandardOutput;
    }

    private string Workspace() => Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;

    // The image lives outside the workspace, like the attachment store (AD-29).
    private Attachment Image()
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "attachments", "J000001")).FullName,
            "A000001.png");
        if (!File.Exists(path)) File.WriteAllBytes(path, RedBluePng(256, 128));
        return new Attachment("A000001", 1, AttachmentKind.Image, "image/png", path, new FileInfo(path).Length, 256,
            128, "cores.png");
    }

    // A real RGB PNG: the left half red, the right half blue.
    private static byte[] RedBluePng(int width, int height)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = y * (1 + width * 3) + 1 + x * 3;
            if (x < width / 2) raw[offset] = 255;
            else raw[offset + 2] = 255;
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        png.Write(number);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    public void Dispose() => Directory.Delete(root, true);

    private sealed class LiveCliFactAttribute : FactAttribute
    {
        public LiveCliFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("DANTE_LIVE_CLI") != "1")
                Skip = "Evidência com as CLIs reais; rode com DANTE_LIVE_CLI=1.";
        }
    }
}
