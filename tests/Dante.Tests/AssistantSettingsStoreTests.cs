using Dante.Worker.Agents;
using Dante.Worker.Sessions;
using Dante.Worker.Settings;

namespace Dante.Tests;

public sealed class AssistantSettingsStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-settings-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("null")]
    [InlineData("{\"bad-user\":{\"Claude\":\"high\"}}")]
    [InlineData("{\"123\":{\"unknown\":\"high\"}}")]
    [InlineData("{\"123\":{\"Claude\":null}}")]
    [InlineData("{\"123\":{\"Claude\":\"high low\"}}")]
    public void InvalidEffortMapStopsLoading(string map)
    {
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "settings.json");
        File.WriteAllText(file, "{\"DefaultAgent\":\"Claude\",\"Efforts\":" + map + "}");
        Assert.Throws<InvalidDataException>(() => new AssistantSettingsStore(file));
    }

    [Fact]
    public void FailedEffortWriteRestoresPreviousPreference()
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        store.SetEffort(123, AgentKind.Claude, "high");
        File.Delete(file);
        Directory.CreateDirectory(file);
        Assert.ThrowsAny<Exception>(() => store.SetEffort(123, AgentKind.Claude, "low"));
        Assert.Equal("high", store.GetEffort(123, AgentKind.Claude));
    }

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
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"ActiveRepositories\": { \"abc\": \"@demo\" } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"ActiveRepositories\": { \"123\": \"demo\" } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"ActiveRepositories\": { \"123\": null } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"ActiveRepositories\": [] }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"ActiveRepositories\": null }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"SessionModes\": null }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"SessionModes\": { \"abc\": \"auto\" } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"SessionModes\": { \"123\": \"full\" } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"SessionModes\": { \"123\": \"approval\" } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"SessionModes\": { \"123\": \"Auto\" } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"SessionModes\": { \"123\": null } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"Models\": null }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"Models\": { \"abc\": { \"Claude\": \"opus\" } } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"Models\": { \"123\": null } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"Models\": { \"123\": { \"Gemini\": \"pro\" } } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"Models\": { \"123\": { \"Claude\": null } } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"Models\": { \"123\": { \"Claude\": \"\" } } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"Models\": { \"123\": { \"Claude\": \"--dangerous\" } } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"Models\": { \"123\": { \"Codex\": \"a b\" } } }")]
    [InlineData("{ \"DefaultAgent\": \"Claude\", \"Models\": { \"123\": \"opus\" } }")]
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
    public void PersistsActiveRepositoryPerUserAcrossInstances()
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        Assert.Null(store.GetActiveRepository(123));
        store.SetActiveRepository(123, "@Demo");
        store.SetActiveRepository(456, "@other");
        store.SetDefaultAgent(AgentKind.Codex);

        var reopened = new AssistantSettingsStore(file);
        Assert.Equal("@demo", reopened.GetActiveRepository(123));
        Assert.Equal("@other", reopened.GetActiveRepository(456));
        Assert.Equal(AgentKind.Codex, reopened.Current.DefaultAgent);

        reopened.SetActiveRepository(123, null);
        Assert.Null(new AssistantSettingsStore(file).GetActiveRepository(123));
        Assert.Equal("@other", new AssistantSettingsStore(file).GetActiveRepository(456));
    }

    [Fact]
    public void PersistsSessionModePerUserAcrossInstances()
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        store.SetDefaultAgent(AgentKind.Codex);
        // Users who never chose a mode keep the file as before.
        Assert.DoesNotContain("SessionModes", File.ReadAllText(file));
        Assert.Equal(AgentPermissionProfile.Manual, store.GetSessionMode(123));
        store.SetSessionMode(123, AgentPermissionProfile.Plan);
        store.SetSessionMode(456, AgentPermissionProfile.Auto);

        var reopened = new AssistantSettingsStore(file);
        Assert.Equal(AgentPermissionProfile.Plan, reopened.GetSessionMode(123));
        Assert.Equal(AgentPermissionProfile.Auto, reopened.GetSessionMode(456));
        Assert.Equal(AgentPermissionProfile.Manual, reopened.GetSessionMode(789));
        Assert.Equal(AgentKind.Codex, reopened.Current.DefaultAgent);
        Assert.Contains("\"plan\"", File.ReadAllText(file));

        reopened.SetSessionMode(123, AgentPermissionProfile.Manual);
        Assert.Equal(AgentPermissionProfile.Manual, new AssistantSettingsStore(file).GetSessionMode(123));
        Assert.Throws<ArgumentOutOfRangeException>(() => reopened.SetSessionMode(123, (AgentPermissionProfile)42));
    }

    [Fact]
    public void PersistsModelPerUserAndAgentAcrossInstances()
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        store.SetDefaultAgent(AgentKind.Codex);
        // Users who never chose a model keep the file as before.
        Assert.DoesNotContain("Models", File.ReadAllText(file));
        Assert.Null(store.GetModel(123, AgentKind.Claude));
        store.SetModel(123, AgentKind.Claude, "opus");
        store.SetModel(123, AgentKind.Codex, "gpt-5.5");
        store.SetModel(456, AgentKind.Claude, "claude-sonnet-5-5");

        var reopened = new AssistantSettingsStore(file);
        Assert.Equal("opus", reopened.GetModel(123, AgentKind.Claude));
        Assert.Equal("gpt-5.5", reopened.GetModel(123, AgentKind.Codex));
        Assert.Equal("claude-sonnet-5-5", reopened.GetModel(456, AgentKind.Claude));
        Assert.Null(reopened.GetModel(456, AgentKind.Codex));
        Assert.Null(reopened.GetModel(789, AgentKind.Claude));
        Assert.Equal(AgentKind.Codex, reopened.Current.DefaultAgent);

        // Back to the CLI default removes the entry; the other agent keeps its model.
        reopened.SetModel(123, AgentKind.Claude, null);
        reopened.SetModel(456, AgentKind.Claude, null);
        var cleared = new AssistantSettingsStore(file);
        Assert.Null(cleared.GetModel(123, AgentKind.Claude));
        Assert.Equal("gpt-5.5", cleared.GetModel(123, AgentKind.Codex));
        Assert.DoesNotContain("456", File.ReadAllText(file));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-m")]
    [InlineData("opus; rm -rf /")]
    [InlineData("opus\nsonnet")]
    public void RejectsInvalidModelNamesWithoutPersisting(string model)
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        Assert.Throws<ArgumentException>(() => store.SetModel(123, AgentKind.Claude, model));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetModel(123, (AgentKind)42, "opus"));
        Assert.Null(store.GetModel(123, AgentKind.Claude));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void FailedModelWriteKeepsPreviousValue()
    {
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var store = new AssistantSettingsStore(Path.Combine(blocker, "settings.json"));
        Assert.ThrowsAny<IOException>(() => store.SetModel(123, AgentKind.Codex, "gpt-5.5"));
        Assert.Null(store.GetModel(123, AgentKind.Codex));
    }

    [Fact]
    public void FailedSessionModeWriteKeepsPreviousValue()
    {
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var store = new AssistantSettingsStore(Path.Combine(blocker, "settings.json"));
        Assert.ThrowsAny<IOException>(() => store.SetSessionMode(123, AgentPermissionProfile.Auto));
        Assert.Equal(AgentPermissionProfile.Manual, store.GetSessionMode(123));
    }

    [Fact]
    public void ClearsActiveRepositoryForEveryUser()
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        store.SetActiveRepository(123, "@demo");
        store.SetActiveRepository(456, "@demo");
        store.SetActiveRepository(789, "@other");

        Assert.Equal(2, store.ClearActiveRepository("@DEMO"));
        Assert.Equal(0, store.ClearActiveRepository("@demo"));
        var reopened = new AssistantSettingsStore(file);
        Assert.Null(reopened.GetActiveRepository(123));
        Assert.Null(reopened.GetActiveRepository(456));
        Assert.Equal("@other", reopened.GetActiveRepository(789));
    }

    [Fact]
    public void LoadsFileWithoutActiveRepositories()
    {
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "settings.json");
        File.WriteAllText(file, "{ \"DefaultAgent\": \"Codex\" }");
        var store = new AssistantSettingsStore(file);
        Assert.Equal(AgentKind.Codex, store.Current.DefaultAgent);
        Assert.Null(store.GetActiveRepository(123));
    }

    [Fact]
    public void RejectsInvalidActiveRepositoryAliasWithoutPersisting()
    {
        var file = Path.Combine(root, "settings.json");
        var store = new AssistantSettingsStore(file);
        Assert.Throws<ArgumentException>(() => store.SetActiveRepository(123, "demo"));
        Assert.Null(store.GetActiveRepository(123));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void FailedActiveRepositoryWriteKeepsPreviousValue()
    {
        Directory.CreateDirectory(root);
        var blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "not a directory");
        var store = new AssistantSettingsStore(Path.Combine(blocker, "settings.json"));
        Assert.ThrowsAny<IOException>(() => store.SetActiveRepository(123, "@demo"));
        Assert.Null(store.GetActiveRepository(123));
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
