using System.Text;
using Dante.Worker.Agents;
using Dante.Worker.Brain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class BrainStorageTests
{
    [Fact]
    public void ScopeRejectsMissingAndEmptyIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => new BrainScope(Guid.Empty, Guid.NewGuid(), Guid.NewGuid()).Validate());
        Assert.Throws<ArgumentException>(() => new BrainScope(Guid.NewGuid(), Guid.Empty, Guid.NewGuid()).Validate());
        Assert.Throws<ArgumentException>(() => new BrainScope(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty).Validate());
        Assert.Throws<ArgumentException>(() => new BrainScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.Empty).Validate());
    }

    [Fact]
    public async Task OriginalsAreFinalizedVerifiedAndRejectTraversalOrTampering()
    {
        var root = Directory.CreateTempSubdirectory("brain-source-test-").FullName;
        try
        {
            var files = new BrainSourceFiles(root);
            var source = await files.PublishAsync(new MemoryStream(Encoding.UTF8.GetBytes("original")));
            await files.VerifyAsync(source);
            Assert.Equal(8, source.Length);
            Assert.DoesNotContain(Directory.EnumerateFiles(root), p => p.EndsWith(".tmp"));
            await Assert.ThrowsAsync<BrainStorageException>(() => files.VerifyAsync(source with { Reference = "../elsewhere" }));
            await File.WriteAllTextAsync(Path.Combine(root, source.Reference), "alterado");
            await Assert.ThrowsAsync<BrainStorageException>(() => files.VerifyAsync(source));
            File.Delete(Path.Combine(root, source.Reference));
            await Assert.ThrowsAsync<BrainStorageException>(() => files.VerifyAsync(source));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SourcesRejectSymlinkRoots()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("brain-symlink-").FullName;
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), target);
            Assert.Throws<BrainStorageException>(() => new BrainSourceFiles(Path.Combine(root, "link")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BrainIsOptInAndDisabledDoesNotCreateFilesOrReadConnection()
    {
        var services = new ServiceCollection();
        BrainConfiguration.Register(services, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { { "Brain:ConnectionString", "invalid" } }).Build());
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<IBrainStorage>());
    }

    [Fact]
    public void DatabaseCredentialsNeverReachAgentEvenWithExplicitRepositoryOverrides()
    {
        var request = new AgentProcessRequest(AgentKind.Codex, AppContext.BaseDirectory, [],
            EnvironmentVariables: new Dictionary<string, string> { { "Brain__ConnectionString", "synthetic-secret" }, { "PGPASSWORD", "synthetic-secret" }, { "PROJECT", "ok" } });
        Assert.True(AgentProcessStartInfo.TryCreate(request, new Resolver(), out var info, out _));
        Assert.DoesNotContain(info!.Environment.Keys, k => k.StartsWith("Brain__", StringComparison.OrdinalIgnoreCase) || k == "PGPASSWORD");
        Assert.Equal("ok", info.Environment["PROJECT"]);
    }
    private sealed class Resolver : IAgentExecutableResolver
    {
        public string? Resolve(AgentKind agent) => "/synthetic/codex";
    }
}
