using System.Diagnostics;
using Dante.Domain.Agentes;
using Dante.Infrastructure.Agentes;
using Dante.ProcessProbe;

namespace Dante.Tests;

public sealed class ContencaoNativaTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly TimeSpan Prazo = TimeSpan.FromSeconds(20);
    private static readonly string Runtime = Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json");
    private static readonly string Probe = typeof(ProbeMarker).Assembly.Location;
    private static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
        (OperatingSystem.IsWindows() ? "C:\\Program Files\\dotnet\\dotnet.exe" : "/usr/bin/dotnet");

    [Fact]
    public void PadraoEstritoECompatibilidadeSoPorOpcaoExplicita()
    {
        Assert.Equal(ModoContencaoAgentes.Estrito, new OpcoesContencaoAgentes().Modo);
    }

    [Theory]
    [InlineData(null, ModoContencaoAgentes.Estrito)]
    [InlineData("", ModoContencaoAgentes.Estrito)]
    [InlineData("strict", ModoContencaoAgentes.Estrito)]
    [InlineData("compatibility", ModoContencaoAgentes.Compatibilidade)]
    public void InterpretacaoDeModoEExplicita(string? valor, ModoContencaoAgentes esperado) =>
        Assert.Equal(esperado, OpcoesContencaoAgentes.InterpretarModo(valor).Modo);

    [Fact]
    public void ValorDesconhecidoNaoAtivaModoDegradado() =>
        Assert.Throws<AgentProcessStartException>(() => OpcoesContencaoAgentes.InterpretarModo("auto"));

    [Fact]
    public async Task CompatibilidadeExplicitaInformaGarantiaInferior()
    {
        await using var ator = await new InteractiveAgentProcessLauncher(new Resolver(Dotnet),
            new OpcoesContencaoAgentes { Modo = ModoContencaoAgentes.Compatibilidade }).StartAsync(Pedido("interactive"));
        Assert.False(ator.ContencaoNativa);
        Assert.Contains("podem sobreviver", ator.AvisoDeContencao);
        await ator.CloseInputAsync();
        await ator.Completion.WaitAsync(Prazo);
    }

    [Fact]
    public async Task CancelamentoAnteriorNaoIniciaExecutavel()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Estrito().StartAsync(Pedido("interactive"),
            cancellationToken: new CancellationToken(true)));
    }

    [NativeFact]
    public async Task FilhoNascidoAposEOFNaoSobreviveACompletion()
    {
        await using var ator = await Estrito().StartAsync(Pedido("spawn-on-eof", Runtime));
        Assert.True(ator.ContencaoNativa);
        Assert.Null(ator.AvisoDeContencao);
        Assert.Equal("ready", await Linha(ator));
        await ator.CloseInputAsync();
        var filho = await Pid(ator);
        await ator.Completion.WaitAsync(Prazo);
        Assert.False(Vivo(filho));
    }

    [NativeTheory]
    [InlineData("gracioso")]
    [InlineData("principal-inesperado")]
    [InlineData("forcado")]
    public async Task MultiplosNiveisNaoSobrevivemAoEncerramento(string modo)
    {
        var filhos = new List<int>();
        var identidades = new Dictionary<int, DateTime?>();
        await using var ator = await Estrito().StartAsync(Pedido("tree-until-eof", Runtime, "4"));
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var id = int.Parse(await Linha(ator));
                filhos.Add(id); identidades[id] = Inicio(id);
            }
            Assert.All(filhos, id => Assert.True(Vivo(id)));
            if (modo == "gracioso") await ator.CloseInputAsync();
            else if (modo == "forcado") ator.Kill();
            else
            {
                using var principal = Process.GetProcessById(ator.ProcessId);
                principal.Kill();
            }
            await ator.Completion.WaitAsync(Prazo);
            Assert.All(filhos, id => Assert.False(Vivo(id)));
        }
        finally { foreach (var id in filhos) LimparSeVivo(id, identidades.GetValueOrDefault(id)); }
    }

    [NativeFact]
    public async Task LancamentosConcorrentesCancelamentoEParadaRepetidaSaoIsolados()
    {
        var atores = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Estrito().StartAsync(Pedido("interactive"))));
        try
        {
            foreach (var ator in atores) Assert.Equal("ready", await Linha(ator));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => atores[0].WriteLineAsync("nao", new CancellationToken(true)));
            atores[0].Kill();
            await atores[0].Completion.WaitAsync(Prazo);
            Assert.All(atores.Skip(1), ator => Assert.False(ator.Completion.IsCompleted));
            await Task.WhenAll(atores.Skip(1).Select(ator => ator.StopAsync(TimeSpan.FromSeconds(2))));
            foreach (var ator in atores)
            {
                ator.Kill();
                await ator.StopAsync(TimeSpan.Zero);
                Assert.False(Vivo(ator.ProcessId));
            }
        }
        finally { foreach (var ator in atores) await ator.DisposeAsync(); }
    }

    [NativeFact]
    public async Task ContencaoPreservaArgumentosLiteraisEAmbienteFiltrado()
    {
        const string texto = "espacos; $(nao executar) $HOME \\";
        await using var ator = await Estrito().StartAsync(Pedido("interactive") with { IsGeneral = true });
        Assert.Equal("ready", await Linha(ator));
        await ator.WriteLineAsync(texto);
        Assert.Equal("echo:" + texto, await Linha(ator));
        await ator.CloseInputAsync();
        await ator.Completion.WaitAsync(Prazo);
        await using var env = await Estrito().StartAsync(Pedido("env-until-eof", "DBUS_SESSION_BUS_ADDRESS") with { IsGeneral = true });
        Assert.Equal("<unset>", await Linha(env));
        await env.CloseInputAsync();
        await env.Completion.WaitAsync(Prazo);
    }

    [LinuxNativeFact]
    public async Task FalhaDeCriacaoNaoExecutaProbeNemFazFallback()
    {
        await Assert.ThrowsAsync<AgentProcessStartException>(() => Estrito().StartAsync(Pedido("interactive") with
        {
            EnvironmentVariables = new Dictionary<string, string> { ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/tmp/dante-bus-inexistente-" + Guid.NewGuid().ToString("N"),
                ["XDG_RUNTIME_DIR"] = "/tmp/dante-runtime-inexistente-" + Guid.NewGuid().ToString("N") }
        }));
    }

    [LinuxNativeFact]
    public async Task CancelamentoNaBarreiraNaoLiberaCLI()
    {
        Assert.True(AgentProcessStartInfo.TryCreate(Pedido("interactive"), new Resolver(Dotnet), out var start, out _));
        using var cancelar = new CancellationTokenSource();
        var passouPelaBarreira = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContencaoSystemd.IniciarAsync(start, cancelar.Token, (diagnostico, token) =>
        {
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(diagnostico));
            passouPelaBarreira = true;
            cancelar.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }));
        Assert.True(passouPelaBarreira);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("outro:123")]
    [InlineData("bootstrap:0")]
    [InlineData("bootstrap:-1")]
    [InlineData("bootstrap:123:456")]
    [InlineData("bootstrap:99999999999999999")]
    public void HandshakeInvalidoNaoIdentificaBootstrap(string? linha) =>
        Assert.Throws<AgentProcessStartException>(() => ContencaoSystemd.LerPidDoHandshake(linha, "bootstrap"));

    [Fact]
    public void IdentidadeDivergenteRecusaOBootstrapAntesDaLiberacao()
    {
        using var processo = Process.GetCurrentProcess();
        Assert.Throws<AgentProcessStartException>(() => ContencaoSystemd.ValidarIdentidadeBootstrap(
            processo, processo.Id, processo.StartTime.AddSeconds(-1)));
    }

    [LinuxNativeFact]
    public async Task BootstrapAnunciaPidRealDentroDoScopeAntesDeExecutarCLI()
    {
        var marcador = Path.Combine(Path.GetTempPath(), "dante-cli-ainda-nao-liberada-" + Guid.NewGuid().ToString("N"));
        Assert.True(AgentProcessStartInfo.TryCreate(Pedido("marcar-inicio", marcador), new Resolver(Dotnet), out var start, out _));
        DiagnosticoBootstrapLinux? observado = null;
        var contido = await ContencaoSystemd.IniciarAsync(start, CancellationToken.None, (diagnostico, token) =>
        {
            observado = diagnostico;
            Assert.False(File.Exists(marcador));
            Assert.Equal(diagnostico.PidBootstrap, diagnostico.PidDoPidfd);
            Assert.Equal(diagnostico.PidCliente, diagnostico.PidBootstrap); // Medido via handshake, não inferido.
            Assert.Contains("/" + diagnostico.Unidade, diagnostico.Cgroup);
            Assert.Equal("T", diagnostico.Estado);
            Assert.Contains(File.ReadAllLines($"/proc/{diagnostico.PidBootstrap}/status"), l => l.StartsWith("State:\tT"));
            Assert.Contains("0::" + diagnostico.Cgroup, File.ReadAllLines($"/proc/{diagnostico.PidBootstrap}/cgroup"));
            using var bootstrap = Process.GetProcessById(diagnostico.PidBootstrap);
            Assert.Equal(diagnostico.InicioUtc, bootstrap.StartTime.ToUniversalTime());
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(diagnostico));
            return Task.CompletedTask;
        });
        await using var ator = new InteractiveAgentProcess(contido.Processo, null, contido: contido);
        try
        {
            Assert.Equal("ready", await Linha(ator));
            Assert.Equal(ator.ProcessId.ToString(), File.ReadAllText(marcador));
            Assert.NotNull(observado);
            await ator.CloseInputAsync();
            await ator.Completion.WaitAsync(Prazo);
            Assert.True(((ContencaoSystemd)contido.Contencao).EstaVazia());
            output.WriteLine("Contenção vazia confirmada após Completion.");
        }
        finally { File.Delete(marcador); }
    }

    [LinuxNativeFact]
    public async Task SaidaDoBootstrapNaBarreiraFalhaSemExecutarCLI()
    {
        Assert.True(AgentProcessStartInfo.TryCreate(Pedido("interactive"), new Resolver(Dotnet), out var start, out _));
        await Assert.ThrowsAsync<AgentProcessStartException>(() => ContencaoSystemd.IniciarAsync(start, CancellationToken.None,
            async (diagnostico, token) =>
            {
                output.WriteLine(System.Text.Json.JsonSerializer.Serialize(diagnostico));
                using var bootstrap = Process.GetProcessById(diagnostico.PidBootstrap);
                Assert.Equal(diagnostico.InicioUtc, bootstrap.StartTime.ToUniversalTime());
                bootstrap.Kill();
                await bootstrap.WaitForExitAsync(token);
            }));
    }

    [LinuxNativeFact]
    public async Task TimeoutNaBarreiraNaoLiberaCLI()
    {
        Assert.True(AgentProcessStartInfo.TryCreate(Pedido("interactive"), new Resolver(Dotnet), out var start, out _));
        var erro = await Assert.ThrowsAsync<AgentProcessStartException>(() => ContencaoSystemd.IniciarAsync(start, CancellationToken.None,
            async (diagnostico, token) =>
            {
                output.WriteLine(System.Text.Json.JsonSerializer.Serialize(diagnostico));
                await Task.Delay(Timeout.Infinite, token);
            }));
        Assert.IsAssignableFrom<OperationCanceledException>(erro.InnerException);
    }

    [LinuxNativeFact]
    public async Task DuploForkESetsidReparentamFilhosSemEscaparDoScope()
    {
        var script = "echo ready; read inicio; /bin/sh -c 'echo $$; /usr/bin/setsid /bin/sh -c \"echo \\$\\$; /bin/sleep 60 & echo \\$!; wait\" </dev/null &'; read fim";
        var launcher = new InteractiveAgentProcessLauncher(new Resolver("/bin/sh"), new());
        await using var ator = await launcher.StartAsync(new AgentProcessRequest(AgentKind.Codex, AppContext.BaseDirectory, ["-c", script]));
        Assert.Equal("ready", await Linha(ator));
        await ator.WriteLineAsync("iniciar");
        var intermediario = await Pid(ator);
        var neto = await Pid(ator);
        var bisneto = await Pid(ator);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { principal = ator.ProcessId, intermediario, neto, bisneto }));
        var grupo = File.ReadAllLines($"/proc/{ator.ProcessId}/cgroup").Single(l => l.StartsWith("0::"));
        Assert.Contains(grupo, File.ReadAllLines($"/proc/{neto}/cgroup"));
        Assert.Contains(grupo, File.ReadAllLines($"/proc/{bisneto}/cgroup"));
        // Aguarda a saída do intermediário; não confunde processo ainda encerrando com reparentamento concluído.
        var limite = DateTime.UtcNow + Prazo;
        while (Vivo(intermediario))
        {
            Assert.True(DateTime.UtcNow < limite);
            await Task.Delay(20);
        }
        Assert.DoesNotContain(File.ReadAllLines($"/proc/{neto}/status"), l => l == "PPid:\t" + intermediario);
        using (var raiz = Process.GetProcessById(ator.ProcessId)) raiz.Kill();
        await ator.Completion.WaitAsync(Prazo);
        Assert.False(Vivo(neto)); Assert.False(Vivo(bisneto));
        output.WriteLine("Filhos reparentados encerrados antes de Completion concluir com sucesso.");
    }

    [LinuxNativeFact]
    public async Task CgroupProprioFicaVazioAposCompletion()
    {
        await using var ator = await Estrito().StartAsync(Pedido("interactive"));
        Assert.Equal("ready", await Linha(ator));
        var grupo = File.ReadAllLines($"/proc/{ator.ProcessId}/cgroup").Single(l => l.StartsWith("0::"))[3..];
        Assert.Contains("/dante-agent-", grupo);
        Assert.EndsWith(".scope", grupo);
        var caminho = "/sys/fs/cgroup" + grupo;
        Assert.Contains("populated 1", File.ReadAllLines(Path.Combine(caminho, "cgroup.events")));
        await ator.CloseInputAsync();
        await ator.Completion.WaitAsync(Prazo);
        if (Directory.Exists(caminho)) Assert.Contains("populated 0", File.ReadAllLines(Path.Combine(caminho, "cgroup.events")));
        else Assert.True(Directory.Exists(Path.GetDirectoryName(caminho)));
    }

    [NativeFact]
    public async Task ExecutavelInexistenteFalhaExplicitamente()
    {
        var launcher = new InteractiveAgentProcessLauncher(new Resolver(Path.Combine(AppContext.BaseDirectory, "nao-existe")), new());
        await Assert.ThrowsAsync<AgentProcessStartException>(() => launcher.StartAsync(Pedido("interactive")));
    }

    [LinuxNativeFact]
    public async Task ShellEAuxiliaresContinuamContidos()
    {
        var launcher = new InteractiveAgentProcessLauncher(new Resolver("/bin/sh"), new());
        await using var ator = await launcher.StartAsync(new AgentProcessRequest(AgentKind.Codex, AppContext.BaseDirectory,
            ["-c", "echo ready; read resposta; /usr/bin/setsid /bin/sleep 60 & echo $!; exit 0"]));
        Assert.Equal("ready", await Linha(ator));
        await ator.WriteLineAsync("continuar");
        var filho = await Pid(ator);
        var inicio = Inicio(filho);
        try { await ator.Completion.WaitAsync(Prazo); Assert.False(Vivo(filho)); }
        finally { LimparSeVivo(filho, inicio); }
    }

    private static InteractiveAgentProcessLauncher Estrito() => new(new Resolver(Dotnet), new());
    private static AgentProcessRequest Pedido(params string[] args) => new(AgentKind.Codex, AppContext.BaseDirectory,
        ["exec", "--runtimeconfig", Runtime, Probe, .. args]);
    private static async Task<string> Linha(InteractiveAgentProcess ator)
    {
        using var prazo = new CancellationTokenSource(Prazo);
        while (await ator.Output.WaitToReadAsync(prazo.Token))
            while (ator.Output.TryRead(out var linha)) if (linha.Stream == AgentOutputStream.StandardOutput) return linha.Text;
        throw new InvalidOperationException("Saída encerrada antes da linha esperada.");
    }
    private static async Task<int> Pid(InteractiveAgentProcess ator)
    {
        // Filhos herdam stdout: o ready do filho pode preceder o PID escrito pelo pai.
        while (true) if (int.TryParse(await Linha(ator), out var id)) return id;
    }
    private static bool Vivo(int id)
    {
        try
        {
            using var p = Process.GetProcessById(id);
            if (p.HasExited) return false;
            // Zumbi não executa e não pertence mais ao cgroup; o reaping cabe ao novo pai.
            if (OperatingSystem.IsLinux() && File.ReadAllText($"/proc/{id}/stat").Split(')')[1].TrimStart().StartsWith('Z')) return false;
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static DateTime? Inicio(int id)
    {
        try { using var p = Process.GetProcessById(id); return p.StartTime; }
        catch (ArgumentException) { return null; }
    }
    private static void LimparSeVivo(int id, DateTime? inicio)
    {
        if (inicio is null || !Vivo(id)) return;
        using var p = Process.GetProcessById(id);
        if (p.StartTime == inicio) { p.Kill(); p.WaitForExit(5000); }
    }
    private sealed class Resolver(string executavel) : IAgentExecutableResolver { public string Resolve(AgentKind agente) => executavel; }
}

internal class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DANTE_TEST_NATIVE_CONTAINMENT") != "1")
            Skip = "Integração nativa exige laboratório autorizado: DANTE_TEST_NATIVE_CONTAINMENT=1.";
    }
}
internal sealed class NativeTheoryAttribute : TheoryAttribute
{
    public NativeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("DANTE_TEST_NATIVE_CONTAINMENT") != "1")
            Skip = "Integração nativa exige laboratório autorizado: DANTE_TEST_NATIVE_CONTAINMENT=1.";
    }
}

internal sealed class LinuxNativeFactAttribute : NativeFactAttribute
{
    public LinuxNativeFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Cenário específico de cgroups/systemd Linux.";
    }
}

public sealed class ContratoContencaoTests
{
    [Fact]
    public async Task CompletionAguardaConfirmacaoNativaMesmoDepoisDaSaidaDaRaiz()
    {
        using var raw = IniciarProbe();
        var entrou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contencao = new FakeContencao(async token => { entrou.SetResult(); await confirmar.Task.WaitAsync(token); });
        var consultas = 0;
        await using var ator = new InteractiveAgentProcess(raw, null,
            observarDescendentes: (_, _) => { consultas++; throw new Exception("Polling não permitido."); },
            contido: new ProcessoContido(raw, contencao, raw.StandardInput, raw.StandardOutput, raw.StandardError));
        await ator.CloseInputAsync();
        await entrou.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(ator.Completion.IsCompleted);
        confirmar.SetResult();
        await ator.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, consultas);
    }

    [Fact]
    public async Task TimeoutNativoFalhaExplicitamenteEDisposeLiberaRecursos()
    {
        using var raw = IniciarProbe();
        var contencao = new FakeContencao(token => Task.Delay(Timeout.Infinite, token));
        var ator = new InteractiveAgentProcess(raw, null, prazoDaLimpeza: TimeSpan.FromMilliseconds(50),
            contido: new ProcessoContido(raw, contencao, raw.StandardInput, raw.StandardOutput, raw.StandardError));
        await ator.CloseInputAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => ator.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<TimeoutException>(() => ator.DisposeAsync().AsTask());
        Assert.True(contencao.Liberada);
    }

    [Fact]
    public async Task ErroDeObservacaoNativaNaoFazFallbackParaPolling()
    {
        using var raw = IniciarProbe();
        var contencao = new FakeContencao(_ => throw new IOException("observação recusada"));
        var ator = new InteractiveAgentProcess(raw, null,
            contido: new ProcessoContido(raw, contencao, raw.StandardInput, raw.StandardOutput, raw.StandardError));
        await ator.CloseInputAsync();
        await Assert.ThrowsAsync<IOException>(() => ator.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<IOException>(() => ator.DisposeAsync().AsTask());
        Assert.True(contencao.Liberada);
    }

    private static Process IniciarProbe()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
            (OperatingSystem.IsWindows() ? "C:\\Program Files\\dotnet\\dotnet.exe" : "/usr/bin/dotnet"))
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json"),
                     typeof(ProbeMarker).Assembly.Location, "interactive" }) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }
    private sealed class FakeContencao(Func<CancellationToken, Task> encerrar) : IContencaoDeAgente
    {
        internal bool Liberada { get; private set; }
        public Task EncerrarEConfirmarVaziaAsync(CancellationToken token) => encerrar(token);
        public void Dispose() => Liberada = true;
    }
}

public sealed class ContencaoWindowsTests
{
    [WindowsNativeFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task FecharUltimoHandleDoJobEncerraRaizEFilhos()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "C:\\Program Files\\dotnet\\dotnet.exe")
        {
            WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false,
            StandardInputEncoding = new System.Text.UTF8Encoding(false)
        };
        foreach (var arg in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json"),
                     typeof(ProbeMarker).Assembly.Location, "spawn", Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json") })
            start.ArgumentList.Add(arg);
        var contido = ContencaoWindows.Iniciar(start, CancellationToken.None);
        try
        {
            using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            int pid;
            while (!int.TryParse(await contido.Saida.ReadLineAsync(prazo.Token), out pid)) prazo.Token.ThrowIfCancellationRequested();
            using var filho = Process.GetProcessById(pid);
            contido.Contencao.Dispose();
            await contido.Processo.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await filho.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(filho.HasExited);
        }
        finally
        {
            contido.Contencao.Dispose();
            contido.Entrada.Dispose(); contido.Saida.Dispose(); contido.Erro.Dispose(); contido.Processo.Dispose();
        }
    }
}
internal sealed class WindowsNativeFactAttribute : NativeFactAttribute
{
    public WindowsNativeFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Integração Job Objects exige runner Windows.";
    }
}
