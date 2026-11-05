using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dante.Infrastructure.Agentes;

// Um escopo aleatório por lançamento, nunca o cgroup do Worker. systemd associa o bootstrap antes do exec.
internal sealed record DiagnosticoBootstrapLinux(int PidCliente, int PidBootstrap, int PidDoPidfd,
    DateTime InicioUtc, string Unidade, string Cgroup, string Estado);

internal sealed class ContencaoSystemd(string unidade, string caminhoCgroup) : IContencaoDeAgente
{
    internal string CaminhoCgroup => caminhoCgroup;
    private const string Raiz = "/sys/fs/cgroup";

    internal static async Task<ProcessoContido> IniciarAsync(ProcessStartInfo agente, CancellationToken cancellationToken,
        Func<DiagnosticoBootstrapLinux, CancellationToken, Task>? antesDeLiberar = null)
    {
        if (access(agente.FileName, 1) != 0)
            throw new AgentProcessStartException("Executável do agente inexistente ou sem permissão de execução.");
        if (!File.Exists(Raiz + "/cgroup.controllers"))
            throw new AgentProcessStartException("Contenção estrita exige cgroups v2 e systemd de usuário disponíveis.");
        var unidade = $"dante-agent-{Guid.NewGuid():N}.scope";
        var identificadorHandshake = Guid.NewGuid().ToString("N");
        // O script é fixo. Executável e argumentos passam por "$@", nunca por interpolação shell.
        // A barreira STOP permite confirmar cgroup + identidade antes de liberar qualquer código da CLI.
        var limparRuntime = !agente.Environment.ContainsKey("XDG_RUNTIME_DIR");
        var limparBus = !agente.Environment.ContainsKey("DBUS_SESSION_BUS_ADDRESS");
        var script = "printf '%s:%s\\n' \"$1\" \"$$\"; shift; kill -STOP $$; " + (limparRuntime ? "unset XDG_RUNTIME_DIR; " : "") +
                     (limparBus ? "unset DBUS_SESSION_BUS_ADDRESS; " : "") + "exec \"$@\"";
        var comando = new ProcessStartInfo("/usr/bin/systemd-run")
        {
            UseShellExecute = false, WorkingDirectory = agente.WorkingDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = agente.StandardInputEncoding,
            StandardOutputEncoding = agente.StandardOutputEncoding, StandardErrorEncoding = agente.StandardErrorEncoding
        };
        comando.Environment.Clear();
        foreach (var (nome, valor) in agente.Environment) comando.Environment[nome] = valor;
        AdicionarConexaoAoGerenciador(comando);
        foreach (var arg in new[] { "--user", "--scope", "--quiet", "--no-ask-password", "--expand-environment=no",
                     "--description=Agente DANTE isolado", "--unit=" + unidade,
                     "--property=KillMode=control-group", "--property=SendSIGKILL=yes",
                     "--property=TimeoutStopSec=3s", "--", "/bin/sh", "-c", script, "dante-bootstrap", identificadorHandshake, agente.FileName })
            comando.ArgumentList.Add(arg);
        foreach (var arg in agente.ArgumentList) comando.ArgumentList.Add(arg);
        var processo = new Process { StartInfo = comando };
        ContencaoSystemd? contencao = null;
        var iniciado = false;
        var transferido = false;
        SafeFileHandle? pidfd = null;
        try
        {
            if (!processo.Start()) throw new AgentProcessStartException("Não foi possível iniciar o bootstrap contido.");
            iniciado = true;
            using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            prazo.CancelAfter(TimeSpan.FromSeconds(5));
            // A primeira linha é exclusiva do bootstrap, antes de qualquer código da CLI. Não chega ao protocolo.
            var linhaHandshake = await processo.StandardOutput.ReadLineAsync(prazo.Token);
            var pidBootstrap = LerPidDoHandshake(linhaHandshake, identificadorHandshake);
            // --scope usa exec. Igualdade é verificada por mensagem explícita, não presumida a partir de Start().
            // Outra implementação de systemd-run que crie PID distinto é recusada antes de liberar a CLI.
            if (pidBootstrap != processo.Id || processo.HasExited)
                throw new AgentProcessStartException("PID anunciado pelo bootstrap diverge do processo original do scope.");
            var inicioBootstrap = processo.StartTime;
            pidfd = new SafeFileHandle(pidfd_open(pidBootstrap, 0), ownsHandle: true);
            if (pidfd.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            var pidDoPidfd = int.Parse(File.ReadAllLines($"/proc/self/fdinfo/{pidfd.DangerousGetHandle().ToInt32()}")
                .Single(l => l.StartsWith("Pid:", StringComparison.Ordinal))[4..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            if (pidDoPidfd != pidBootstrap)
                throw new AgentProcessStartException("pidfd não corresponde ao bootstrap anunciado.");
            while (true)
            {
                prazo.Token.ThrowIfCancellationRequested();
                ValidarIdentidadeBootstrap(processo, pidBootstrap, inicioBootstrap);
                var cgroup = File.ReadAllLines($"/proc/{pidBootstrap}/cgroup")
                    .SingleOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal))?[3..];
                var parado = File.ReadAllLines($"/proc/{pidBootstrap}/status")
                    .Any(l => l.StartsWith("State:\tT", StringComparison.Ordinal));
                if (parado && cgroup is not null && cgroup.EndsWith("/" + unidade, StringComparison.Ordinal))
                {
                    var caminho = Path.GetFullPath(Raiz + cgroup);
                    if (!caminho.StartsWith(Raiz + "/", StringComparison.Ordinal))
                        throw new AgentProcessStartException("Caminho do cgroup inválido.");
                    contencao = new ContencaoSystemd(unidade, caminho);
                    if (contencao.EstaVazia()) throw new AgentProcessStartException("Bootstrap não está na contenção.");
                    var diagnostico = new DiagnosticoBootstrapLinux(processo.Id, pidBootstrap, pidDoPidfd,
                        inicioBootstrap.ToUniversalTime(), unidade, cgroup, "T");
                    if (antesDeLiberar is not null) await antesDeLiberar(diagnostico, prazo.Token);
                    ValidarIdentidadeBootstrap(processo, pidBootstrap, inicioBootstrap);
                    if (!File.ReadAllLines($"/proc/{pidBootstrap}/status").Any(l => l.StartsWith("State:\tT", StringComparison.Ordinal)) ||
                        !File.ReadAllLines($"/proc/{pidBootstrap}/cgroup").Contains("0::" + cgroup))
                        throw new AgentProcessStartException("Estado ou cgroup do bootstrap mudou antes da liberação.");
                    // pidfd fixa a identidade mesmo se o PID numérico for reciclado durante o cancelamento.
                    prazo.Token.ThrowIfCancellationRequested();
                    if (pidfd_send_signal(pidfd, 18, IntPtr.Zero, 0) != 0)
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                    var resultado = new ProcessoContido(processo, contencao, processo.StandardInput, processo.StandardOutput, processo.StandardError);
                    transferido = true;
                    return resultado;
                }
                await Task.Delay(20, prazo.Token);
            }
        }
        catch (Exception falha)
        {
            var erroLimpeza = await LimpezaDaInicializacao.TentarAsync(falha,
                async token =>
                {
                    if (!iniciado || processo.HasExited) return;
                    if (pidfd is { IsInvalid: false })
                    {
                        var resultadoSinal = pidfd_send_signal(pidfd, 9, IntPtr.Zero, 0);
                        var erroSinal = resultadoSinal == 0 ? 0 : Marshal.GetLastPInvokeError();
                        if (resultadoSinal != 0 && !processo.HasExited)
                            throw new System.ComponentModel.Win32Exception(erroSinal,
                                "Falha ao sinalizar o bootstrap pela identidade original.");
                    }
                    else processo.Kill(); // Objeto original; nenhuma CLI foi liberada antes da identificação.
                    await processo.WaitForExitAsync(token);
                },
                async token =>
                {
                    if (iniciado) await PararUnidadeAsync(unidade, token);
                },
                async token =>
                {
                    if (!iniciado) return; // Process.Start falhou; nenhuma solicitação ao manager ocorreu.
                    // Stop pode ter resgatado uma falha anterior de sinal/espera; comprova também saída da raiz.
                    await ConfirmarUnidadeVaziaAsync(unidade, contencao, token);
                    await processo.WaitForExitAsync(token);
                });
            if (erroLimpeza is not null)
                throw new AgentProcessStartException("Inicialização/limpeza do scope " + unidade +
                    " falhou; consulte a falha original e as etapas de limpeza.",
                    erroLimpeza);
            if (falha is OperationCanceledException && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException("Lançamento contido cancelado; raiz encerrada e contenção vazia confirmadas.", falha, cancellationToken);
            throw new AgentProcessStartException("Não foi possível estabelecer contenção systemd; lançamento recusado.", falha);
        }
        finally { pidfd?.Dispose(); if (!transferido) processo.Dispose(); }
    }

    internal static int LerPidDoHandshake(string? linha, string identificador)
    {
        var prefixo = identificador + ":";
        if (linha is null || !linha.StartsWith(prefixo, StringComparison.Ordinal) ||
            !int.TryParse(linha.AsSpan(prefixo.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            throw new AgentProcessStartException("Handshake do bootstrap ausente ou inválido; CLI não liberada.");
        return pid;
    }

    internal static void ValidarIdentidadeBootstrap(Process processo, int pidBootstrap, DateTime inicio)
    {
        if (processo.HasExited || ProcessTree.TryGetStartTime(pidBootstrap) != inicio)
            throw new AgentProcessStartException("Bootstrap encerrou ou sua identidade mudou antes da liberação.");
    }

    internal bool EstaVazia()
    {
        try
        {
            var linha = File.ReadAllLines(Path.Combine(caminhoCgroup, "cgroup.events"))
                .Single(l => l.StartsWith("populated ", StringComparison.Ordinal));
            return linha switch { "populated 0" => true, "populated 1" => false,
                _ => throw new IOException("Estado populated do cgroup inválido.") };
        }
        catch (IOException) when (FoiRemovido())
        {
            // Um cgroup removido pelo gerenciador só pode ter sido removido depois de ficar vazio.
            return true;
        }
    }

    private bool FoiRemovido()
    {
        try { _ = File.GetAttributes(caminhoCgroup); return false; }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Inclui ENODEV na leitura de cgroup.events aberto enquanto o manager remove o cgroup.
            // Não usa Directory.Exists: acesso negado/erro de I/O não pode parecer diretório ausente.
            _ = File.GetAttributes(Path.GetDirectoryName(caminhoCgroup)!);
            _ = File.GetAttributes(Raiz + "/cgroup.controllers");
            return true;
        }
    }

    public async Task EncerrarEConfirmarVaziaAsync(CancellationToken cancellationToken)
    {
        if (EstaVazia()) return;
        try { await ExecutarControleAsync(["stop", unidade], cancellationToken); }
        catch (IOException) when (EstaVazia()) { return; } // O último processo pode sair antes do pedido de stop.
        if (!EstaVazia()) throw new IOException("systemd concluiu a parada, mas o cgroup do agente ainda está ocupado.");
    }

    private static async Task PararUnidadeAsync(string unidade, CancellationToken token)
    {
        try { await ExecutarControleAsync(["stop", unidade], token); }
        catch (IOException erroStop)
        {
            // Unidade já removida não precisa de stop. Exige resposta positiva do manager, não erro genérico.
            string estado;
            try { estado = await ExecutarControleAsync(["show", unidade, "--property=LoadState", "--value"], token); }
            catch (Exception erroConsulta)
            {
                throw new AggregateException("Stop e consulta da unidade falharam.", erroStop, erroConsulta);
            }
            if (estado.Trim() != "not-found") throw;
        }
    }

    private static async Task ConfirmarUnidadeVaziaAsync(string unidade, ContencaoSystemd? contencao, CancellationToken token)
    {
        if (contencao is not null)
        {
            if (!contencao.EstaVazia()) throw new IOException("Scope " + unidade + " ainda populated após tentativa de stop.");
            return;
        }
        // Falhas anteriores ao handshake podem não ter capturado o caminho; consulta só a unidade exclusiva.
        var resposta = await ExecutarControleAsync(["show", unidade, "--property=LoadState", "--property=ActiveState", "--property=ControlGroup"], token);
        var campos = resposta.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p.Length == 2 ? p[1] : "");
        if (campos.GetValueOrDefault("LoadState") == "not-found") return;
        if (!campos.TryGetValue("ControlGroup", out var grupo)) throw new IOException("Manager não informou o cgroup da unidade.");
        if (grupo.Length == 0 && campos.GetValueOrDefault("ActiveState") is "inactive" or "failed") return;
        var caminho = Path.GetFullPath(Raiz + grupo);
        if (!grupo.EndsWith("/" + unidade, StringComparison.Ordinal) || !caminho.StartsWith(Raiz + "/", StringComparison.Ordinal))
            throw new IOException("Manager informou cgroup inesperado para a unidade exclusiva.");
        if (!new ContencaoSystemd(unidade, caminho).EstaVazia())
            throw new IOException("Scope " + unidade + " ainda populated após tentativa de stop.");
    }

    private static void AdicionarConexaoAoGerenciador(ProcessStartInfo comando)
    {
        // Só o bootstrap recebe a conexão ao gerenciador. Os valores acrescentados são removidos antes do exec.
        comando.Environment.TryAdd("XDG_RUNTIME_DIR", "/run/user/" + geteuid());
        comando.Environment.TryAdd("DBUS_SESSION_BUS_ADDRESS", "unix:path=" + comando.Environment["XDG_RUNTIME_DIR"] + "/bus");
    }

    private static async Task<string> ExecutarControleAsync(string[] argumentos, CancellationToken token)
    {
        var start = new ProcessStartInfo("/usr/bin/systemctl")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        AdicionarConexaoAoGerenciador(start);
        start.ArgumentList.Add("--user");
        start.ArgumentList.Add("--no-ask-password");
        foreach (var arg in argumentos) start.ArgumentList.Add(arg);
        using var processo = Process.Start(start) ?? throw new IOException("Falha ao iniciar controle da contenção.");
        // Drenagem concorrente, sem publicar mensagens que possam conter dados do agente.
        var saida = processo.StandardOutput.ReadToEndAsync(token);
        var erro = processo.StandardError.ReadToEndAsync(token);
        try
        {
            await processo.WaitForExitAsync(token);
            await Task.WhenAll(saida, erro);
            if (processo.ExitCode != 0) throw new IOException("Controle da unidade de contenção falhou (código " + processo.ExitCode + ").");
            return await saida;
        }
        finally
        {
            if (!processo.HasExited) { processo.Kill(); await processo.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
        }
    }

    public void Dispose() { }
    [DllImport("libc", SetLastError = true)] private static extern int pidfd_open(int pid, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern int pidfd_send_signal(SafeFileHandle pidfd, int sig, IntPtr info, uint flags);
    [DllImport("libc")] private static extern uint geteuid();
    [DllImport("libc", SetLastError = true)] private static extern int access(string path, int mode);
}
