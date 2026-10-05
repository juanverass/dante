using Dante.Infrastructure.Contextos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dante.Tests;

public sealed class WorkerLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "dante-worker-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WorkerStartsAndStopsWithHostCancellation()
    {
        using var host = BuildHost(Path.Combine(root, "settings.json"));
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync();
        Assert.True(lifetime.ApplicationStarted.IsCancellationRequested);

        await host.StopAsync();
        Assert.True(lifetime.ApplicationStopped.IsCancellationRequested);
    }

    [Fact]
    public async Task WorkerFailsToStartWithInvalidSettings()
    {
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "settings.json");
        File.WriteAllText(file, "{ \"DefaultAgent\": \"Gemini\" }");
        using var host = BuildHost(file);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => host.StartAsync());
        Assert.Contains(file, exception.Message);
        Assert.False(lifetime.ApplicationStarted.IsCancellationRequested);
    }

    private static IHost BuildHost(string settingsFile)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(_ => new AssistantSettingsStore(settingsFile));
        builder.Services.AddHostedService<Dante.Worker.Worker>();
        return builder.Build();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
