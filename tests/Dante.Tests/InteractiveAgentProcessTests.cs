using System.Diagnostics;
using Dante.Infrastructure.Agentes;
using Dante.ProcessProbe;

namespace Dante.Tests;

public sealed class InteractiveAgentProcessTests
{
    private static readonly string ProbeAssembly = typeof(ProbeMarker).Assembly.Location;
    private static readonly string RuntimeConfig = Path.Combine(
        AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task OutputArrivesBeforeTheProcessExitsAndInputReachesTheSameProcess()
    {
        await using var process = await StartAsync("interactive");

        // The probe only exits after "exit", so these lines are read while it is still running.
        Assert.Equal("ready", await NextStdoutAsync(process));
        await process.WriteLineAsync("first");
        Assert.Equal("echo:first", await NextStdoutAsync(process));
        await process.WriteLineAsync("text with spaces; $(echo unsafe) & more");
        Assert.Equal("echo:text with spaces; $(echo unsafe) & more", await NextStdoutAsync(process));
        Assert.False(process.Completion.IsCompleted);

        await process.WriteLineAsync("exit");
        var exit = await process.Completion.WaitAsync(Timeout);

        Assert.Equal(0, exit.ExitCode);
        Assert.False(exit.Killed);
        Assert.Equal(InteractiveAgentProcessState.Exited, process.State);
    }

    [Fact]
    public async Task ConcurrentWritesDoNotInterleave()
    {
        await using var process = await StartAsync("interactive");
        Assert.Equal("ready", await NextStdoutAsync(process));
        var lines = Enumerable.Range(0, 64).Select(index => $"{index}:" + new string((char)('a' + index % 26), 8_192))
            .ToArray();

        var reader = Task.Run(async () =>
        {
            var received = new List<string>();
            while (received.Count < lines.Length)
            {
                received.Add(await NextStdoutAsync(process));
            }

            return received;
        });
        await Task.WhenAll(lines.Select(line => Task.Run(() => process.WriteLineAsync(line))));
        var echoed = await reader.WaitAsync(Timeout);

        Assert.Equal(lines.Select(line => "echo:" + line).Order(), echoed.Order());
    }

    [Fact]
    public async Task SlowReaderReceivesEveryLineInOrder()
    {
        const int count = 5_000;
        await using var process = await StartAsync("burst", count.ToString());

        var index = 0;
        await foreach (var line in process.Output.ReadAllAsync().WithCancellation(Cancel()))
        {
            if (index % 1_000 == 0)
            {
                // Lets the bounded output fill up so the pumps have to wait for the reader.
                await Task.Delay(50);
            }

            Assert.Equal(AgentOutputStream.StandardOutput, line.Stream);
            Assert.Equal($"line {index++}", line.Text);
        }

        Assert.Equal(count, index);
        Assert.Equal(0, (await process.Completion.WaitAsync(Timeout)).ExitCode);
    }

    [Fact]
    public async Task StandardErrorAndExitCodeAreReported()
    {
        await using var process = await StartAsync("interactive");
        Assert.Equal("ready", await NextStdoutAsync(process));

        await process.WriteLineAsync("fail");
        var lines = await ReadToEndAsync(process);
        var exit = await process.Completion.WaitAsync(Timeout);

        Assert.Contains(new AgentOutputLine(AgentOutputStream.StandardError, "probe failed"), lines);
        Assert.Equal(3, exit.ExitCode);
    }

    [Fact]
    public async Task RedactionIsAppliedToEveryOutputLine()
    {
        await using var process = await StartAsync(line => line.Replace("secret-value", "[redacted]"), "interactive");
        Assert.Equal("ready", await NextStdoutAsync(process));

        await process.WriteLineAsync("token=secret-value");

        Assert.Equal("echo:token=[redacted]", await NextStdoutAsync(process));
    }

    [Fact]
    public async Task InputWithLineBreakIsRejectedWithoutCorruptingTheStream()
    {
        await using var process = await StartAsync("interactive");
        Assert.Equal("ready", await NextStdoutAsync(process));

        await Assert.ThrowsAsync<ArgumentException>(() => process.WriteLineAsync("one\nexit"));
        await process.WriteLineAsync("still framed");

        Assert.Equal("echo:still framed", await NextStdoutAsync(process));
    }

    [Fact]
    public async Task ClosingInputEndsTheProcessCleanlyAndRejectsLaterInput()
    {
        await using var process = await StartAsync("interactive");
        Assert.Equal("ready", await NextStdoutAsync(process));

        await process.CloseInputAsync();

        Assert.Equal(InteractiveAgentProcessState.InputClosed, process.State);
        await Assert.ThrowsAsync<AgentProcessInputClosedException>(() => process.WriteLineAsync("late"));
        Assert.Equal("stdin closed", await NextStdoutAsync(process));
        var exit = await process.Completion.WaitAsync(Timeout);
        Assert.Equal(0, exit.ExitCode);
        Assert.False(exit.Killed);
    }

    [Fact]
    public async Task InputAfterExitIsRejected()
    {
        await using var process = await StartAsync("interactive");
        Assert.Equal("ready", await NextStdoutAsync(process));

        await process.WriteLineAsync("exit");
        await process.Completion.WaitAsync(Timeout);

        await Assert.ThrowsAsync<AgentProcessInputClosedException>(() => process.WriteLineAsync("late"));
    }

    [Fact]
    public async Task WritesRacingWithExitEitherSucceedOrReportClosedInput()
    {
        await using var process = await StartAsync("interactive");
        Assert.Equal("ready", await NextStdoutAsync(process));

        var writers = Enumerable.Range(0, 200).Select(index => Task.Run(async () =>
        {
            try
            {
                await process.WriteLineAsync(index == 20 ? "exit" : $"line {index}");
                return true;
            }
            catch (AgentProcessInputClosedException)
            {
                return false;
            }
        })).ToArray();
        await Task.WhenAll(writers).WaitAsync(Timeout);
        await process.Completion.WaitAsync(Timeout);

        await Assert.ThrowsAsync<AgentProcessInputClosedException>(() => process.WriteLineAsync("late"));
    }

    [Fact]
    public async Task StopRacingWithWritesLeavesNoRunningProcess()
    {
        var process = await StartAsync("interactive");
        await using var _ = process;
        Assert.Equal("ready", await NextStdoutAsync(process));

        var writers = Enumerable.Range(0, 200).Select(index => Task.Run(async () =>
        {
            try
            {
                await process.WriteLineAsync($"line {index}");
            }
            catch (AgentProcessInputClosedException)
            {
            }
        })).ToArray();
        var stop = process.StopAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(writers).WaitAsync(Timeout);
        await stop.WaitAsync(Timeout);

        Assert.Equal(InteractiveAgentProcessState.Exited, process.State);
        Assert.True(HasExited(process.ProcessId));
        await Assert.ThrowsAsync<AgentProcessInputClosedException>(() => process.WriteLineAsync("late"));
    }

    [Fact]
    public async Task StopKillsProcessThatIgnoresClosedInput()
    {
        await using var process = await StartAsync("wait");
        Assert.Equal("ready", await NextStdoutAsync(process));

        var exit = await process.StopAsync(TimeSpan.FromMilliseconds(300)).WaitAsync(Timeout);

        Assert.True(exit.Killed);
        Assert.Equal(InteractiveAgentProcessState.Exited, process.State);
        Assert.True(HasExited(process.ProcessId));
    }

    [Fact]
    public async Task StopAndDisposeLeaveNoOrphanedChildProcess()
    {
        var stopped = await StartAsync("spawn", RuntimeConfig);
        var stoppedChild = int.Parse(await NextStdoutAsync(stopped));
        await stopped.StopAsync(TimeSpan.FromMilliseconds(300)).WaitAsync(Timeout);
        await stopped.DisposeAsync();

        var disposed = await StartAsync("spawn", RuntimeConfig);
        var disposedChild = int.Parse(await NextStdoutAsync(disposed));
        await disposed.DisposeAsync().AsTask().WaitAsync(Timeout);

        await WaitUntilExitedAsync(stoppedChild);
        await WaitUntilExitedAsync(disposedChild);
    }

    [Fact]
    public async Task GracefulExitDoesNotLeaveOrphanedChildProcess()
    {
        await using var stopped = await StartAsync("spawn-until-eof", RuntimeConfig);
        var stoppedChild = int.Parse(await NextStdoutAsync(stopped));
        var exit = await stopped.StopAsync(Timeout).WaitAsync(Timeout);

        await using var closed = await StartAsync("spawn-until-eof", RuntimeConfig);
        var closedChild = int.Parse(await NextStdoutAsync(closed));
        await closed.CloseInputAsync();
        await closed.Completion.WaitAsync(Timeout);

        // The parent left on its own; its child, still running when it exited, was killed.
        Assert.False(exit.Killed);
        Assert.Equal(0, exit.ExitCode);
        Assert.True(HasExited(stoppedChild));
        Assert.True(HasExited(closedChild));
        // The pipes the child inherited are released, so the output completes as well.
        await ReadToEndAsync(stopped).WaitAsync(Timeout);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ConclusaoConfirmaSaidaDeTodosOsDescendentesEPermiteEncerrarNovamente(int quantidade)
    {
        await using var process = await StartAsync("spawn-until-eof", RuntimeConfig, quantidade.ToString());
        var filhos = new List<int>();
        for (var i = 0; i < quantidade; i++) filhos.Add(int.Parse(await NextStdoutAsync(process)));
        var result = await process.StopAsync(Timeout).WaitAsync(Timeout);
        Assert.False(result.Killed); Assert.Equal(0, result.ExitCode);
        Assert.All(filhos, pid => Assert.True(HasExited(pid)));
        Assert.Equal(result, await process.StopAsync(Timeout));
        await process.CloseInputAsync();
        await ReadToEndAsync(process).WaitAsync(Timeout);
        await process.DisposeAsync();
        await process.DisposeAsync();
    }

    [Fact]
    public async Task DescendenteQueSaiNaturalmenteAntesDoPrincipalNaoImpedeLimpeza()
    {
        await using var process = await StartAsync("spawn-until-eof", RuntimeConfig, "1", "50");
        var child = int.Parse(await NextStdoutAsync(process));
        await WaitUntilExitedAsync(child);
        Assert.False(process.Completion.IsCompleted);
        await process.CloseInputAsync();
        Assert.Equal(0, (await process.Completion.WaitAsync(Timeout)).ExitCode);
        Assert.True(HasExited(child));
    }

    [Fact]
    public async Task CompletionEsperaConfirmacaoDosDescendentesMesmoComPrincipalEncerrado()
    {
        var entrou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liberar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var process = IniciarComLimpeza(async (pid, inicio, ct) =>
        {
            entrou.TrySetResult();
            await liberar.Task.WaitAsync(ct);
            await ProcessTree.EncerrarEAguardarAsync(pid, inicio, ct);
        });
        var child = int.Parse(await NextStdoutAsync(process));
        try
        {
            await process.CloseInputAsync();
            await entrou.Task.WaitAsync(Timeout);
            Assert.True(HasExited(process.ProcessId));
            Assert.False(process.Completion.IsCompleted);
            Assert.False(HasExited(child));
        }
        finally { liberar.TrySetResult(); }
        await process.Completion.WaitAsync(Timeout);
        Assert.True(HasExited(child));
        await ReadToEndAsync(process).WaitAsync(Timeout);
    }

    [Fact]
    public async Task TimeoutDaLimpezaFalhaExplicitamenteELiberaRecursosAoDescartar()
    {
        var process = IniciarComLimpeza(async (pid, inicio, ct) =>
        {
            await ProcessTree.EncerrarEAguardarAsync(pid, inicio, ct);
            // Simula a confirmação que não chega, sem deixar um processo real sobrevivente.
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
        }, TimeSpan.FromMilliseconds(200));
        var child = int.Parse(await NextStdoutAsync(process));
        await process.CloseInputAsync();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => process.Completion.WaitAsync(Timeout));
        Assert.Contains("saída não confirmada", error.Message);
        Assert.True(HasExited(child));
        Assert.True(HasExited(process.ProcessId));
        await Assert.ThrowsAsync<TimeoutException>(() => process.DisposeAsync().AsTask());
        await process.DisposeAsync();
        await ReadToEndAsync(process).WaitAsync(Timeout);
    }

    [Fact]
    public async Task IdentidadeDivergenteNuncaEncerraOutroProcessoEProcessoJaEncerradoEhAceito()
    {
        using var child = IniciarProbe("wait");
        var inicio = child.StartTime;
        try
        {
            await ProcessTree.EncerrarEAguardarAsync(child.Id, inicio.AddSeconds(-1), CancellationToken.None);
            Assert.False(child.HasExited);
        }
        finally { child.Kill(); await child.WaitForExitAsync(); }
        await ProcessTree.EncerrarEAguardarAsync(child.Id, inicio, CancellationToken.None);
    }

    [Fact]
    public async Task CapturaFinalAguardaDescobertaEmAndamentoIncluindoFilhoCriadoNaUltimaJanela()
    {
        using var entrou = new ManualResetEventSlim();
        using var consultar = new ManualResetEventSlim();
        using var capturou = new ManualResetEventSlim();
        using var liberar = new ManualResetEventSlim();
        using var raw = IniciarProbe("spawn-on-command", RuntimeConfig);
        var chamadas = 0;
        await using var process = new InteractiveAgentProcess(raw, null, observarDescendentes: (pid, inicio) =>
        {
            var ultima = Interlocked.Increment(ref chamadas) == 2;
            if (ultima)
            {
                entrou.Set();
                if (!consultar.Wait(Timeout)) throw new TimeoutException("Teste não liberou a consulta.");
            }
            var encontrados = ProcessTree.GetDescendants(pid, inicio);
            if (ultima)
            {
                capturou.Set();
                if (!liberar.Wait(Timeout)) throw new TimeoutException("Teste não liberou a descoberta.");
            }
            return encontrados;
        });
        Assert.Equal("ready", await NextStdoutAsync(process));
        var fechar = Task.Run(process.CloseInputAsync);
        int child;
        try
        {
            Assert.True(await Task.Run(() => entrou.Wait(Timeout)));
            // O filho nasce depois que a última observação já começou.
            await raw.StandardInput.WriteLineAsync("spawn");
            await raw.StandardInput.FlushAsync();
            child = int.Parse(await NextStdoutAsync(process));
            consultar.Set();
            Assert.True(await Task.Run(() => capturou.Wait(Timeout)));
            raw.StandardInput.Close();
            await raw.WaitForExitAsync().WaitAsync(Timeout);
            Assert.False(process.Completion.IsCompleted);
        }
        finally { consultar.Set(); liberar.Set(); }
        await fechar.WaitAsync(Timeout);
        await process.Completion.WaitAsync(Timeout);
        Assert.True(HasExited(child));
        await ReadToEndAsync(process).WaitAsync(Timeout);
    }

    [Fact]
    public async Task IdentidadeInicialIndisponivelNaoEhListaVaziaNemAutorizaMatarPorPid()
    {
        using var raw = IniciarProbe("interactive");
        var consultou = false;
        var matou = false;
        var process = new InteractiveAgentProcess(raw, null,
            encerrarDescendente: (_, _, _) => { matou = true; return Task.CompletedTask; },
            obterIdentidade: _ => null,
            observarDescendentes: (_, _) => { consultou = true; return []; });
        Assert.Equal("ready", await NextStdoutAsync(process));
        await process.CloseInputAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => process.Completion.WaitAsync(Timeout));
        Assert.Contains("Identidade inicial", error.InnerException!.Message);
        Assert.False(consultou); Assert.False(matou);
        await Assert.ThrowsAsync<InvalidOperationException>(() => process.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task FalhaDeObservacaoNaoEhTratadaComoAusenciaDeDescendentes()
    {
        using var raw = IniciarProbe("interactive");
        var process = new InteractiveAgentProcess(raw, null,
            observarDescendentes: (_, _) => throw new UnauthorizedAccessException("falha simulada"));
        Assert.Equal("ready", await NextStdoutAsync(process));
        await process.CloseInputAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => process.Completion.WaitAsync(Timeout));
        Assert.IsType<UnauthorizedAccessException>(error.InnerException);
        await Assert.ThrowsAsync<InvalidOperationException>(() => process.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task PrincipalMuitoRapidoNaoPermiteDeclararLimpezaSemObservacaoInicial()
    {
        using var raw = IniciarProbe("spawn-and-exit", RuntimeConfig);
        var childId = int.Parse((await raw.StandardOutput.ReadLineAsync().WaitAsync(Timeout))!);
        using var child = Process.GetProcessById(childId);
        try
        {
            await raw.WaitForExitAsync().WaitAsync(Timeout);
            var process = new InteractiveAgentProcess(raw, null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => process.Completion.WaitAsync(Timeout));
            Assert.False(child.HasExited); // Não há identidade observada que autorize a limpeza desse filho.
            await Assert.ThrowsAsync<InvalidOperationException>(() => process.DisposeAsync().AsTask());
        }
        finally
        {
            // O teste mantém o próprio handle; a limpeza de fallback não é atribuída ao código sob teste.
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync().WaitAsync(Timeout);
        }
    }

    private static InteractiveAgentProcess IniciarComLimpeza(Func<int, DateTime, CancellationToken, Task> limpeza,
        TimeSpan? prazo = null) => new(IniciarProbe("spawn-until-eof", RuntimeConfig), null, limpeza, prazo);

    private static Process IniciarProbe(params string[] argumentos)
    {
        var start = new ProcessStartInfo(FindDotnetHost())
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argumento in new[] { "exec", "--runtimeconfig", RuntimeConfig, ProbeAssembly }.Concat(argumentos))
            start.ArgumentList.Add(argumento);
        return Process.Start(start)!;
    }

    [Fact]
    public async Task RedactionFailureFailsTheOutputAndKillsTheProcess()
    {
        var failure = new InvalidOperationException("redaction failed");
        await using var process = await StartAsync(
            line => line.StartsWith("echo:boom") ? throw failure : line, "interactive");
        Assert.Equal("ready", await NextStdoutAsync(process));

        await process.WriteLineAsync("boom");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadToEndAsync(process));
        Assert.Same(failure, thrown);
        Assert.True((await process.Completion.WaitAsync(Timeout)).Killed);
    }

    [Fact]
    public async Task CancelledWriteWaitDoesNotBreakTheStream()
    {
        await using var process = await StartAsync("interactive");
        Assert.Equal("ready", await NextStdoutAsync(process));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => process.WriteLineAsync("never", new CancellationToken(canceled: true)));
        await process.WriteLineAsync("after cancel");

        Assert.Equal("echo:after cancel", await NextStdoutAsync(process));
    }

    [Fact]
    public async Task GeneralProcessDoesNotInheritProjectEnvironment()
    {
        const string name = "DANTE_INTERACTIVE_SECRET_TEST";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "project-value");
        try
        {
            var launcher = CreateLauncher();
            await using var ordinary = await launcher.StartAsync(Request("env", name));
            await using var general = await launcher.StartAsync(Request("env", name) with { IsGeneral = true });

            Assert.Equal("project-value", await NextStdoutAsync(ordinary));
            Assert.Equal("<unset>", await NextStdoutAsync(general));
        }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }

    [Fact]
    public async Task StartFailuresAreReportedWithoutStartingAProcess()
    {
        var missing = new InteractiveAgentProcessLauncher(new FixedResolver(null));
        var unstartable = new InteractiveAgentProcessLauncher(
            new FixedResolver(Path.Combine(AppContext.BaseDirectory, "missing-agent-executable.exe")));

        var unavailable = await Assert.ThrowsAsync<AgentProcessStartException>(
            () => missing.StartAsync(Request("interactive")));
        var startFailure = await Assert.ThrowsAsync<AgentProcessStartException>(
            () => unstartable.StartAsync(Request("interactive")));
        var badDirectory = await Assert.ThrowsAsync<AgentProcessStartException>(
            () => CreateLauncher().StartAsync(Request("interactive") with { WorkingDirectory = "relative" }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateLauncher().StartAsync(Request("interactive"), cancellationToken: new CancellationToken(true)));

        Assert.Contains("unavailable", unavailable.Message);
        Assert.Contains("could not be started", startFailure.Message);
        Assert.Contains("Working directory", badDirectory.Message);
    }

    private static Task<InteractiveAgentProcess> StartAsync(params string[] arguments) =>
        CreateLauncher().StartAsync(Request(arguments));

    private static Task<InteractiveAgentProcess> StartAsync(Func<string, string> redact, params string[] arguments) =>
        CreateLauncher().StartAsync(Request(arguments), redact);

    private static async Task<string> NextStdoutAsync(InteractiveAgentProcess process)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (true)
        {
            var line = await process.Output.ReadAsync(cts.Token);
            if (line.Stream == AgentOutputStream.StandardOutput)
            {
                return line.Text;
            }
        }
    }

    private static async Task<List<AgentOutputLine>> ReadToEndAsync(InteractiveAgentProcess process)
    {
        var lines = new List<AgentOutputLine>();
        await foreach (var line in process.Output.ReadAllAsync(Cancel()))
        {
            lines.Add(line);
        }

        return lines;
    }

    private static CancellationToken Cancel() => new CancellationTokenSource(Timeout).Token;

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task WaitUntilExitedAsync(int processId)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!HasExited(processId))
        {
            Assert.True(DateTime.UtcNow < deadline, $"Child process {processId} is still running.");
            await Task.Delay(100);
        }
    }

    private static InteractiveAgentProcessLauncher CreateLauncher() =>
        new(new FixedResolver(FindDotnetHost()));

    private static AgentProcessRequest Request(params string[] arguments) =>
        new(AgentKind.Codex, AppContext.BaseDirectory,
            ["exec", "--runtimeconfig", RuntimeConfig, ProbeAssembly, .. arguments]);

    private static string FindDotnetHost()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("dotnet host must be on PATH to run process tests.");
    }

    private sealed record FixedResolver(string? Executable) : IAgentExecutableResolver
    {
        public string? Resolve(AgentKind agent) => Executable;
    }
}
