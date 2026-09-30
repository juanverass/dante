using Dante.Worker.Agents;
using Dante.Worker.Settings;

namespace Dante.Tests;

public sealed class AssistantSettingsStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-settings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void UsesDocumentedDefaultWithoutCreatingFile()
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        Assert.Equal(AgentKind.Claude, store.Current.DefaultAgent);
        Assert.Equal(AssistantSettings.Default, store.Current);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void PersistsDefaultAgentAcrossInstances()
    {
        var file = Path.Combine(root, "config", "settings.json");
        var store = new AssistantSettingsStore(file);
        Assert.Equal(AgentKind.Codex, store.SetDefaultAgent(AgentKind.Codex).DefaultAgent);
        Assert.Equal(AgentKind.Codex, store.Current.DefaultAgent);
        Assert.True(File.Exists(file));
        Assert.Contains("\"Codex\"", File.ReadAllText(file));

        var reopened = new AssistantSettingsStore(file);
        Assert.Equal(AgentKind.Codex, reopened.Current.DefaultAgent);
        reopened.SetDefaultAgent(AgentKind.Claude);
        Assert.Equal(AgentKind.Claude, new AssistantSettingsStore(file).Current.DefaultAgent);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{ \"DefaultAgent\": null }")]
    [InlineData("{ \"DefaultAgent\": \"Gemini\" }")]
    [InlineData("{ \"DefaultAgent\": \"1\" }")]
    [InlineData("{ \"DefaultAgent\": 1 }")]
    public void RejectsInvalidOrCorruptedFileWithoutChoosingAgent(string content)
    {
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "settings.json");
        File.WriteAllText(file, content);
        var exception = Assert.Throws<InvalidDataException>(() => new AssistantSettingsStore(file));
        Assert.Contains(file, exception.Message);
        Assert.Equal(content, File.ReadAllText(file));
    }

    [Fact]
    public void RejectsUndefinedAgentWithoutPersisting()
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetDefaultAgent((AgentKind)42));
        Assert.Equal(AgentKind.Claude, store.Current.DefaultAgent);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void FailedWriteKeepsPreviousSettings()
    {
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var store = new AssistantSettingsStore(Path.Combine(blocker, "settings.json"));
        Assert.ThrowsAny<IOException>(() => store.SetDefaultAgent(AgentKind.Codex));
        Assert.Equal(AgentKind.Claude, store.Current.DefaultAgent);
    }

    [Fact]
    public void IgnoresLeftoverTemporaryFileFromInterruptedWrite()
    {
        var file = Path.Combine(root, "settings.json");
        new AssistantSettingsStore(file).SetDefaultAgent(AgentKind.Codex);
        File.WriteAllText(Path.Combine(root, ".settings.json.interrupted.tmp"), "{ \"DefaultAgent\": \"Cla");
        Assert.Equal(AgentKind.Codex, new AssistantSettingsStore(file).Current.DefaultAgent);
    }

    [Fact]
    public void ConcurrentWritesLeaveValidFile()
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        Parallel.For(0, 50, index => store.SetDefaultAgent(index % 2 == 0 ? AgentKind.Claude : AgentKind.Codex));
        Assert.Equal(store.Current, new AssistantSettingsStore(file).Current);
        Assert.Single(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData("claude", AgentKind.Claude)]
    [InlineData("CODEX", AgentKind.Codex)]
    [InlineData("Codex", AgentKind.Codex)]
    public void ParsesKnownAgentsCaseInsensitively(string value, AgentKind expected)
    {
        Assert.True(AssistantSettingsStore.TryParseAgent(value, out var agent));
        Assert.Equal(expected, agent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gemini")]
    [InlineData("0")]
    [InlineData(" claude")]
    public void RejectsUnknownAgentNames(string? value)
    {
        Assert.False(AssistantSettingsStore.TryParseAgent(value, out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
