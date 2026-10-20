using System.Diagnostics;
using Dante.Application.Contextos;

namespace Dante.Infrastructure.Contextos;

// Read-only git queries with a fixed ArgumentList and no shell (AD-03, AD-58). core.fsmonitor is off so a repository
// config cannot run a command through `git status`; each query has its own timeout and kills git on expiry.
public sealed class LeitorDeCheckoutsGit(TimeSpan? limite = null) : ILeitorDeCheckouts
{
    private readonly TimeSpan limite = limite ?? TimeSpan.FromSeconds(5);

    public async Task<EstadoDoCheckout> LerAsync(string caminho, CancellationToken cancellationToken = default)
    {
        // Works on an unborn branch too; empty means a detached HEAD.
        var branch = await GitAsync(caminho, cancellationToken, "branch", "--show-current");
        if (branch == string.Empty) branch = "(HEAD destacado)";
        if (branch is null)
            return new EstadoDoCheckout(null, null, null, false, "não foi possível ler o Git deste checkout");
        var status = await GitAsync(caminho, cancellationToken, "status", "--porcelain");
        var alterados = status?.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        // "<behind>\t<ahead>" against the upstream; no upstream means nothing was pushed from this branch yet.
        var divergencia = await GitAsync(caminho, cancellationToken, "rev-list", "--left-right", "--count", "@{upstream}...HEAD");
        var partes = divergencia?.Split('\t', ' ');
        int? naoEnviados = partes is { Length: 2 } && int.TryParse(partes[1], out var aFrente) ? aFrente : null;
        return new EstadoDoCheckout(branch, alterados, naoEnviados, naoEnviados is not null,
            status is null ? "não foi possível ler as alterações" : null);
    }

    private async Task<string?> GitAsync(string caminho, CancellationToken cancellationToken, params string[] argumentos)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = caminho,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argumento in (string[])["-c", "core.fsmonitor=false", "-c", "core.quotepath=off", .. argumentos])
            start.ArgumentList.Add(argumento);
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prazo.CancelAfter(limite);
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null) return null;
            var saida = process.StandardOutput.ReadToEndAsync(prazo.Token);
            _ = process.StandardError.ReadToEndAsync(prazo.Token);
            await process.WaitForExitAsync(prazo.Token);
            return process.ExitCode == 0 ? (await saida).Trim() : null;
        }
        catch (Exception exception) when (exception is OperationCanceledException or System.ComponentModel.Win32Exception
                                              or InvalidOperationException or DirectoryNotFoundException)
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            return null;
        }
        finally
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process?.Dispose();
        }
    }
}
