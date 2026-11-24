using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Dante.Application.Execucoes;

namespace Dante.Infrastructure.Contextos;

// Leitura dos arquivos da worktree para a entrega (#311, revisão 3 do PR #326), confinada pelo kernel. Verificar o caminho
// e depois abri-lo deixa uma janela: um diretório ou o próprio arquivo trocado por um link entre as duas operações faria
// o D.A.N.T.E. ler bytes de fora da worktree. Aqui não há verificação separada da abertura. A raiz é aberta uma vez, como
// handle, sem link em nenhum componente. Cada caminho é resolvido pelo kernel a partir desse handle, numa só operação
// (openat2), com RESOLVE_BENEATH (nunca acima da raiz), RESOLVE_NO_SYMLINKS (nenhum link, em nenhum componente),
// RESOLVE_NO_MAGICLINKS e RESOLVE_NO_XDEV (nenhum outro sistema de arquivos montado no caminho). O tipo, o modo e o
// destino de um link são lidos pelo handle aberto (statx e readlinkat com caminho vazio), nunca pelo nome de novo. O
// conteúdo de um arquivo comum vem de uma segunda abertura, com as mesmas restrições, que precisa ser exatamente o mesmo
// inode, ainda arquivo comum e com um único link físico (um hard link poderia apontar para um arquivo de fora da worktree).
// Só Linux (5.6+), x86_64 ou arm64: em outro sistema a entrega é recusada.
internal sealed class LeituraConfinadaDaWorktree : IDisposable
{
    private const int AtFdCwd = -100;
    // O_NOFOLLOW e O_DIRECTORY diferem entre x86_64 e arm64; os demais são os mesmos.
    private static readonly bool Arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
    private const ulong OPath = 0x200000, OCloExec = 0x80000, ONonBlock = 0x800;
    private static readonly ulong ONoFollow = Arm64 ? 0x8000UL : 0x20000UL, ODirectory = Arm64 ? 0x4000UL : 0x10000UL;
    private const ulong Restricao = 0x01 | 0x02 | 0x04 | 0x08; // NO_XDEV | NO_MAGICLINKS | NO_SYMLINKS | BENEATH
    private const int AtEmptyPath = 0x1000, AtSymlinkNoFollow = 0x100, StatxBasico = 0x7ff;
    private const int Enoent = 2, Enotdir = 20, Eloop = 40;
    private const int Tipo = 0xF000, Regular = 0x8000, Diretorio = 0x4000, Link = 0xA000;
    private const long Openat2 = 437; // mesmo número em x86_64 e arm64 (tabela genérica)

    private readonly int raiz;
    // Ponto de intervenção dos testes: (etapa, caminho relativo), chamado antes de cada abertura ("abrir" e "ler").
    private readonly Action<string, string>? intervencao;

    private LeituraConfinadaDaWorktree(int raiz, Action<string, string>? intervencao)
    {
        this.raiz = raiz;
        this.intervencao = intervencao;
    }

    // A raiz é o caminho canônico já validado (ValidarAsync); aberta sem link em nenhum componente, ela é exatamente ele.
    public static LeituraConfinadaDaWorktree Abrir(string caminhoCanonico, Action<string, string>? intervencao = null)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new WorktreeInvalidaException("A entrega lê a worktree com confinamento do kernel, disponível só no Linux " +
                "x86_64 ou arm64.");
        var fd = AbrirSemLinks(AtFdCwd, caminhoCanonico, OPath | ODirectory | OCloExec, 0x02 | 0x04);
        if (fd < 0) throw new WorktreeInvalidaException("Não foi possível abrir a raiz da worktree sem seguir links.");
        return new LeituraConfinadaDaWorktree(fd, intervencao);
    }

    // Um caminho relativo como o add o gravaria. Link: modo 120000 e o destino (bytes crus). Arquivo comum: 100755 se
    // executável pelo dono, senão 100644, e o conteúdo. Nulo (o caminho não existe na worktree, como para o Git) se estiver
    // ausente, for um diretório ou ficar atrás de um link num componente intermediário. Qualquer outra coisa, ou o arquivo
    // trocado entre a classificação e a leitura, recusa (WorktreeInvalidaException): nenhum byte de fora é lido.
    public (string Modo, byte[] Bytes)? Ler(string relativo)
    {
        if (relativo.Length == 0 || relativo.StartsWith('/') || relativo.Split('/').Any(p => p is "" or "." or ".."))
            throw new WorktreeInvalidaException("Caminho fora da forma na worktree.");
        intervencao?.Invoke("abrir", relativo);
        var referencia = AbrirSemLinks(raiz, relativo, OPath | ONoFollow | OCloExec, Restricao);
        if (referencia is -Enoent or -Enotdir or -Eloop) return null;
        if (referencia < 0) throw Recusa(relativo);
        try
        {
            var (modo, _, inode, dispositivo) = Estado(referencia, relativo);
            switch (modo & Tipo)
            {
                case Diretorio:
                    return null;
                case Link:
                    return ("120000", DestinoDoLink(referencia, relativo));
                case Regular:
                    intervencao?.Invoke("ler", relativo);
                    return ((modo & 0x40) != 0 ? "100755" : "100644", Conteudo(relativo, inode, dispositivo));
                default:
                    throw new WorktreeInvalidaException($"{relativo} não é arquivo comum nem link; nada foi lido.");
            }
        }
        finally
        {
            Fechar(referencia);
        }
    }

    private byte[] Conteudo(string relativo, ulong inode, ulong dispositivo)
    {
        // Final trocado por link: ELOOP. Diretório trocado: ENOTDIR/ELOOP. Em todos os casos, recusa.
        var fd = AbrirSemLinks(raiz, relativo, ONonBlock | OCloExec, Restricao);
        if (fd < 0) throw Recusa(relativo);
        try
        {
            var (modo, links, inodeLido, dispositivoLido) = Estado(fd, relativo);
            if ((modo & Tipo) != Regular || inodeLido != inode || dispositivoLido != dispositivo) throw Recusa(relativo);
            if (links != 1)
                throw new WorktreeInvalidaException($"{relativo} tem mais de um link físico; a entrega não o lê.");
            using var bytes = new MemoryStream();
            var buffer = new byte[81920];
            nint lidos;
            while ((lidos = read(fd, buffer, (nuint)buffer.Length)) > 0) bytes.Write(buffer, 0, (int)lidos);
            if (lidos < 0) throw new IOException($"Falha ao ler {relativo}.");
            return bytes.ToArray();
        }
        finally
        {
            Fechar(fd);
        }
    }

    private static byte[] DestinoDoLink(int fd, string relativo)
    {
        var buffer = new byte[4097];
        var tamanho = readlinkat(fd, "", buffer, (nuint)buffer.Length);
        if (tamanho < 0 || tamanho >= buffer.Length) throw Recusa(relativo);
        return buffer[..(int)tamanho];
    }

    // statx pelo próprio handle: tipo e modo, número de links físicos, inode e dispositivo.
    private static (int Modo, uint Links, ulong Inode, ulong Dispositivo) Estado(int fd, string relativo)
    {
        var estado = new byte[256];
        if (statx(fd, "", AtEmptyPath | AtSymlinkNoFollow, StatxBasico, estado) != 0) throw Recusa(relativo);
        return (BinaryPrimitives.ReadUInt16LittleEndian(estado.AsSpan(28)),
            BinaryPrimitives.ReadUInt32LittleEndian(estado.AsSpan(16)),
            BinaryPrimitives.ReadUInt64LittleEndian(estado.AsSpan(32)),
            ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(estado.AsSpan(136)) << 32) |
            BinaryPrimitives.ReadUInt32LittleEndian(estado.AsSpan(140)));
    }

    private static WorktreeInvalidaException Recusa(string relativo) =>
        new($"{relativo} mudou durante a leitura ou não pode ser lido dentro da worktree; nada foi lido.");

    // Devolve o descritor, ou -errno.
    private static int AbrirSemLinks(int diretorio, string caminho, ulong flags, ulong resolucao)
    {
        var como = new OpenHow { Flags = flags, Mode = 0, Resolve = resolucao };
        var resultado = syscall(Openat2, diretorio, caminho, ref como, (nuint)Marshal.SizeOf<OpenHow>());
        return resultado >= 0 ? (int)resultado : -Marshal.GetLastPInvokeError();
    }

    private static void Fechar(int fd)
    {
        if (fd >= 0) close(fd);
    }

    public void Dispose() => Fechar(raiz);

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long numero, int diretorio, [MarshalAs(UnmanagedType.LPUTF8Str)] string caminho,
        ref OpenHow como, nuint tamanho);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int diretorio, [MarshalAs(UnmanagedType.LPUTF8Str)] string caminho, int flags, int mascara,
        byte[] estado);

    [DllImport("libc", SetLastError = true)]
    private static extern nint readlinkat(int diretorio, [MarshalAs(UnmanagedType.LPUTF8Str)] string caminho, byte[] buffer,
        nuint tamanho);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buffer, nuint tamanho);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
