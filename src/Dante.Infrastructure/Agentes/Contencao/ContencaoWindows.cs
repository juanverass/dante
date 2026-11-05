using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dante.Infrastructure.Agentes;

[SupportedOSPlatform("windows")]
internal sealed class ContencaoWindows(SafeFileHandle job) : IContencaoDeAgente
{
    internal static ProcessoContido Iniciar(ProcessStartInfo start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SafeFileHandle? entradaFilho = null, entradaPai = null, saidaFilho = null, saidaPai = null, erroFilho = null, erroPai = null;
        SafeFileHandle? job = null;
        ContencaoWindows? contencao = null;
        var atributos = IntPtr.Zero;
        var listaJob = IntPtr.Zero;
        var listaPipes = IntPtr.Zero;
        var ambiente = IntPtr.Zero;
        Process? processo = null;
        StreamWriter? entrada = null;
        StreamReader? saida = null;
        StreamReader? erro = null;
        InformacaoProcesso info = default;
        var atributosInicializados = false;
        try
        {
            entradaFilho = CriarPipe(out entradaPai, paiLe: false);
            saidaFilho = CriarPipe(out saidaPai, paiLe: true);
            erroFilho = CriarPipe(out erroPai, paiLe: true);
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw Erro("criar Job Object");
            contencao = new ContencaoWindows(job);
            // Não autoriza BREAKAWAY_OK nem SILENT_BREAKAWAY_OK. Filhos herdam o Job por padrão.
            var limites = new LimitesEstendidos { Basicos = new LimitesBasicos { Flags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limites, (uint)Marshal.SizeOf<LimitesEstendidos>()))
                throw Erro("configurar KILL_ON_JOB_CLOSE");
            nuint tamanho = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref tamanho);
            if (tamanho == 0) throw Erro("dimensionar atributos de criação");
            atributos = Marshal.AllocHGlobal(checked((int)tamanho));
            if (!InitializeProcThreadAttributeList(atributos, 2, 0, ref tamanho)) throw Erro("inicializar atributos de criação");
            atributosInicializados = true;
            listaJob = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(listaJob, job.DangerousGetHandle());
            // JOB_LIST faz associação atômica durante CreateProcess, antes de qualquer instrução do agente.
            if (!UpdateProcThreadAttribute(atributos, 0, 0x2000D, listaJob, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Erro("associar Job Object na criação");
            listaPipes = Marshal.AllocHGlobal(3 * IntPtr.Size);
            var handles = new[] { entradaFilho.DangerousGetHandle(), saidaFilho.DangerousGetHandle(), erroFilho.DangerousGetHandle() };
            Marshal.Copy(handles, 0, listaPipes, handles.Length);
            if (!UpdateProcThreadAttribute(atributos, 0, 0x20002, listaPipes, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw Erro("limitar herança de handles");
            var startup = new StartupEx
            {
                Startup = new Startup
                {
                    Tamanho = Marshal.SizeOf<StartupEx>(), Flags = 0x100,
                    Entrada = handles[0], Saida = handles[1], Erro = handles[2]
                }, Atributos = atributos
            };
            var bloco = string.Join('\0', start.Environment.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Where(x => x.Value is not null).Select(x => x.Key + "=" + x.Value)) + "\0\0";
            ambiente = Marshal.StringToHGlobalUni(bloco);
            var linha = new StringBuilder(string.Join(' ', new[] { start.FileName }.Concat(start.ArgumentList).Select(CitarArgumento)));
            token.ThrowIfCancellationRequested();
            if (!CreateProcessW(start.FileName, linha, IntPtr.Zero, IntPtr.Zero, true,
                    0x00080000 | 0x00000400 | 0x08000000 | 0x00000004, ambiente, start.WorkingDirectory, ref startup, out info))
                throw Erro("criar processo contido e suspenso");
            processo = Process.GetProcessById((int)info.IdProcesso);
            _ = processo.SafeHandle; // Abre a identidade original antes de permitir execução.
            if (!IsProcessInJob(info.Processo, job, out var associado) || !associado)
                throw Erro("confirmar associação ao Job Object");
            entrada = new StreamWriter(new FileStream(entradaPai, FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true };
            saida = new StreamReader(new FileStream(saidaPai, FileAccess.Read), Encoding.UTF8);
            erro = new StreamReader(new FileStream(erroPai, FileAccess.Read), Encoding.UTF8);
            token.ThrowIfCancellationRequested();
            if (ResumeThread(info.Thread) == uint.MaxValue) throw Erro("liberar execução do processo contido");
            return new ProcessoContido(processo, contencao, entrada, saida, erro);
        }
        catch (Exception falha)
        {
            // Último handle do Job encerra também um processo criado que nunca chegou a ResumeThread.
            job?.Dispose();
            var espera = info.Processo == IntPtr.Zero ? 0 : WaitForSingleObject(info.Processo, 5000);
            entrada?.Dispose(); saida?.Dispose(); erro?.Dispose();
            entradaPai?.Dispose(); saidaPai?.Dispose(); erroPai?.Dispose();
            processo?.Dispose();
            if (espera != 0) throw new TimeoutException("Falha na inicialização Windows; saída do processo suspenso não confirmada após fechar o Job.", falha);
            throw;
        }
        finally
        {
            entradaFilho?.Dispose(); saidaFilho?.Dispose(); erroFilho?.Dispose();
            if (info.Thread != IntPtr.Zero) CloseHandle(info.Thread);
            if (info.Processo != IntPtr.Zero) CloseHandle(info.Processo);
            if (atributosInicializados) DeleteProcThreadAttributeList(atributos);
            if (atributos != IntPtr.Zero) Marshal.FreeHGlobal(atributos);
            if (listaJob != IntPtr.Zero) Marshal.FreeHGlobal(listaJob);
            if (listaPipes != IntPtr.Zero) Marshal.FreeHGlobal(listaPipes);
            if (ambiente != IntPtr.Zero) Marshal.FreeHGlobal(ambiente);
        }
    }

    public async Task EncerrarEConfirmarVaziaAsync(CancellationToken token)
    {
        if (QuantidadeAtiva() == 0) return;
        if (!TerminateJobObject(job, 1)) throw Erro("encerrar Job Object");
        // Consulta nativa de contagem do grupo, não descoberta de processos por PID.
        // Confirmação limitada pelo prazo do chamador; nunca considera envio do kill como conclusão.
        while (QuantidadeAtiva() != 0) await Task.Delay(25, token);
    }

    private uint QuantidadeAtiva()
    {
        if (!QueryInformationJobObject(job, 1, out var info, (uint)Marshal.SizeOf<Contabilidade>(), IntPtr.Zero))
            throw Erro("observar Job Object");
        return info.Ativos;
    }

    internal static string CitarArgumento(string argumento)
    {
        var texto = new StringBuilder("\"");
        var barras = 0;
        foreach (var c in argumento)
        {
            if (c == '\\') { barras++; continue; }
            if (c == '"') texto.Append('\\', barras * 2 + 1).Append('"');
            else texto.Append('\\', barras).Append(c);
            barras = 0;
        }
        return texto.Append('\\', barras * 2).Append('"').ToString();
    }

    private static SafeFileHandle CriarPipe(out SafeFileHandle pai, bool paiLe)
    {
        var seguranca = new Seguranca { Tamanho = Marshal.SizeOf<Seguranca>(), Herdavel = true };
        if (!CreatePipe(out var leitura, out var escrita, ref seguranca, 0)) throw Erro("criar pipe");
        pai = paiLe ? leitura : escrita;
        var filho = paiLe ? escrita : leitura;
        if (!SetHandleInformation(pai, 1, 0)) { pai.Dispose(); filho.Dispose(); throw Erro("proteger handle do pipe"); }
        return filho;
    }

    public void Dispose() => job.Dispose();
    private static Win32Exception Erro(string operacao) => new(Marshal.GetLastPInvokeError(), "Falha ao " + operacao + ".");
    [StructLayout(LayoutKind.Sequential)] private struct Seguranca { public int Tamanho; public IntPtr Descritor; [MarshalAs(UnmanagedType.Bool)] public bool Herdavel; }
    [StructLayout(LayoutKind.Sequential)] private struct LimitesBasicos { public long TempoProcesso, TempoJob; public uint Flags; public nuint MinWs, MaxWs; public uint LimiteAtivos; public nuint Afinidade; public uint Prioridade, Escalonamento; }
    [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong Leitura, Escrita, Outros, BytesLeitura, BytesEscrita, BytesOutros; }
    [StructLayout(LayoutKind.Sequential)] private struct LimitesEstendidos { public LimitesBasicos Basicos; public Io Io; public nuint MemoriaProcesso, MemoriaJob, PicoProcesso, PicoJob; }
    [StructLayout(LayoutKind.Sequential)] private struct Contabilidade { public long Usuario, Kernel, UsuarioPeriodo, KernelPeriodo; public uint PageFaults, Total, Ativos, Encerrados; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup { public int Tamanho; public string? Reservado, Desktop, Titulo; public uint X, Y, Largura, Altura, CaracteresX, CaracteresY, Fill, Flags; public ushort Mostrar, Reservado2; public IntPtr DadosReservados, Entrada, Saida, Erro; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupEx { public Startup Startup; public IntPtr Atributos; }
    [StructLayout(LayoutKind.Sequential)] private struct InformacaoProcesso { public IntPtr Processo, Thread; public uint IdProcesso, IdThread; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(IntPtr atributos, string? nome);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int classe, ref LimitesEstendidos info, uint tamanho);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job, int classe, out Contabilidade info, uint tamanho, IntPtr retorno);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeFileHandle job, uint codigo);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr atributos, int quantidade, uint flags, ref nuint tamanho);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr atributos, uint flags, nuint atributo, IntPtr valor, nuint tamanho, IntPtr anterior, IntPtr retorno);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr atributos);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string aplicativo, StringBuilder linha, IntPtr processoAtributos, IntPtr threadAtributos, [MarshalAs(UnmanagedType.Bool)] bool herdar, uint flags, IntPtr ambiente, string diretorio, ref StartupEx startup, out InformacaoProcesso info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr processo, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool associado);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle leitura, out SafeFileHandle escrita, ref Seguranca atributos, uint tamanho);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mascara, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milissegundos);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
