using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Dante.Tests;

public sealed class WorkerLifecycleTests
{
    [Fact]
    public async Task WorkerStartsAndStopsWithHostCancellation()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHostedService<Dante.Worker.Worker>();

        using var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync();
        Assert.True(lifetime.ApplicationStarted.IsCancellationRequested);

        await host.StopAsync();
        Assert.True(lifetime.ApplicationStopped.IsCancellationRequested);
    }
}
