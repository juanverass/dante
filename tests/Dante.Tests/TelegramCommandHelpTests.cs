using System.Text.RegularExpressions;
using Dante.Worker.Telegram;

namespace Dante.Tests;

public sealed class TelegramCommandHelpTests
{
    [Fact]
    public void EverySlashCommandRecognizedByTheHandlerHasHelp()
    {
        // Compare against the real handler, so adding a literal command without documenting it fails validation.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Dante.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, "src", "Dante.Worker", "Telegram", "TelegramPollingService.cs"));
        var commands = Regex.Matches(source, "\"(/[a-z-]+)\"").Select(match => match.Groups[1].Value)
            .Distinct().Order().ToArray();
        Assert.Equal(commands, TelegramCommandHelp.Entries.Select(entry => entry.Command).Order().ToArray());
    }

    [Fact]
    public void OverviewIsSplitIntoReadableCategoriesWithValidExamples()
    {
        var messages = TelegramCommandHelp.Messages("");
        Assert.Equal(9, messages.Count);
        Assert.All(messages, message => Assert.InRange(message.Length, 1, 4000));
        var help = string.Join('\n', messages);
        Assert.Contains("/session start codex manual", help);
        Assert.Contains("/codex @exemplo revise o README", help);
        Assert.Contains("/agent set claude", help);
        Assert.Contains("/repo env bind @exemplo API_TOKEN TOKEN_DO_HOST", help);
        Assert.Contains("preferências", help);
        Assert.Contains("one-shot", help);
        Assert.Contains("Fallback contextual", help);
        Assert.Contains("não é preciso decorá-los", help);
        Assert.Contains("Exemplos fictícios", help);
        Assert.All(TelegramCommandHelp.Entries, entry => Assert.All(entry.Examples.Split('\n'), example =>
            Assert.StartsWith(entry.Command, example)));
    }

    [Theory]
    [InlineData("session", "overrides valem só para essa sessão", "/session close S000003")]
    [InlineData("/SESSION", "overrides valem só para essa sessão", "/session stop")]
    [InlineData("use", "Preferência persistente", "/use general")]
    [InlineData("model", "a sessão ativa mantém seu modelo", "/model claude default")]
    [InlineData("effort", "a sessão ativa mantém seu esforço", "/effort codex default")]
    [InlineData("repo", "não sensíveis", "/repo env list @exemplo")]
    [InlineData("input", "separando respostas por |", "/input S000001 T000002 R000004")]
    public void DetailedHelpExplainsSyntaxAndScope(string topic, string explanation, string example)
    {
        var message = Assert.Single(TelegramCommandHelp.Messages(topic));
        Assert.InRange(message.Length, 1, 4000);
        Assert.Contains(explanation, message);
        Assert.Contains(example, message);
        Assert.Contains("Exemplos fictícios", message);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("session extra")]
    public void UnknownHelpTopicDoesNotEchoUserData(string topic)
    {
        var response = Assert.Single(TelegramCommandHelp.Messages(topic));
        Assert.Contains("Use /help", response);
        Assert.DoesNotContain(topic, response);
    }
}
