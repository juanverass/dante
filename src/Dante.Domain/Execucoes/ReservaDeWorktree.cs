using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// Worktree Git exclusiva de uma execução de escrita (AD-71), uma por repositório envolvido: o principal e cada vinculado
// (AD-58) da mesma execução. Caminhos são gravados já canônicos (symlinks resolvidos) pela Infrastructure; o domínio só
// confere forma e transições. Reservas ativas (Reservada, Pronta) ocupam caminho, branch e capacidade; os índices únicos
// parciais do banco impedem duas reservas ativas no mesmo caminho, na mesma branch do mesmo repositório ou no mesmo
// alias da mesma execução. Toda gravação de reserva acontece sob posse do trabalho (AD-67) e usa xmin (AD-43).
public sealed class ReservaDeWorktree : EntidadeBase
{
    public const int TamanhoMaximoDoAlias = 64;
    public const int TamanhoMaximoDoCaminho = 1024;
    public const int TamanhoMaximoDaBranch = 200;
    public const int TamanhoDoSha = 40;
    public const int TamanhoMaximoDaMaquina = 200;

    private static readonly IReadOnlyDictionary<EstadoDaReservaDeWorktree, EstadoDaReservaDeWorktree[]> Transicoes =
        new Dictionary<EstadoDaReservaDeWorktree, EstadoDaReservaDeWorktree[]>
        {
            [EstadoDaReservaDeWorktree.Reservada] =
                [EstadoDaReservaDeWorktree.Pronta, EstadoDaReservaDeWorktree.Liberada, EstadoDaReservaDeWorktree.Falhou],
            [EstadoDaReservaDeWorktree.Pronta] =
            [
                EstadoDaReservaDeWorktree.Liberada, EstadoDaReservaDeWorktree.Falhou,
                EstadoDaReservaDeWorktree.AguardandoDecisao
            ],
            [EstadoDaReservaDeWorktree.AguardandoDecisao] =
            [
                EstadoDaReservaDeWorktree.Pronta, EstadoDaReservaDeWorktree.Liberada, EstadoDaReservaDeWorktree.Falhou
            ],
            [EstadoDaReservaDeWorktree.Liberada] =
                [EstadoDaReservaDeWorktree.Removida, EstadoDaReservaDeWorktree.Preservada, EstadoDaReservaDeWorktree.Falhou],
            [EstadoDaReservaDeWorktree.Preservada] = [],
            [EstadoDaReservaDeWorktree.Removida] = [],
            [EstadoDaReservaDeWorktree.Falhou] = []
        };

    private ReservaDeWorktree()
    {
    }

    public ReservaDeWorktree(Execucao execucao, EtapaDaExecucao etapa, string alias, bool principal,
        string caminhoDoRepositorio, string caminho, string branch, string shaBase, string maquinaDoWorker,
        DateTimeOffset criadaEm)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(etapa);
        if (etapa.IdExecucao != execucao.Id) throw new InvalidOperationException("Etapa de outra execução.");
        if (!execucao.Nova) throw new InvalidOperationException("Só execuções novas reservam worktree.");
        if (execucao.Terminal) throw new InvalidOperationException("Execução encerrada não reserva worktree.");
        if (!etapa.Escrita) throw new InvalidOperationException("Só etapas de escrita reservam worktree.");
        Alias = Execucao.TextoObrigatorio(alias, TamanhoMaximoDoAlias, nameof(alias));
        CaminhoDoRepositorio = Absoluto(caminhoDoRepositorio, nameof(caminhoDoRepositorio));
        Caminho = Absoluto(caminho, nameof(caminho));
        if (string.Equals(CaminhoDoRepositorio, Caminho, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A worktree não pode ser o checkout principal.", nameof(caminho));
        Branch = Execucao.TextoObrigatorio(branch, TamanhoMaximoDaBranch, nameof(branch));
        if (shaBase is not { Length: TamanhoDoSha } || !shaBase.All(Uri.IsHexDigit))
            throw new ArgumentException("SHA base inválido.", nameof(shaBase));
        ShaBase = shaBase.ToLowerInvariant();
        MaquinaDoWorker = Execucao.TextoObrigatorio(maquinaDoWorker, TamanhoMaximoDaMaquina, nameof(maquinaDoWorker));
        IdExecucao = execucao.Id;
        IdEtapa = etapa.Id;
        Principal = principal;
        CriadaEm = criadaEm;
    }

    public Guid IdExecucao { get; private set; }
    // A etapa de escrita que reservou; as etapas seguintes da mesma execução reutilizam a reserva.
    public Guid IdEtapa { get; private set; }
    public string Alias { get; private set; } = "";
    // O repositório principal da execução (o diretório de trabalho do agente); os demais são vinculados.
    public bool Principal { get; private set; }
    // Checkout principal do repositório cadastrado, canônico. Nunca recebe escrita de execução nova.
    public string CaminhoDoRepositorio { get; private set; } = "";
    public string Caminho { get; private set; } = "";
    public string Branch { get; private set; } = "";
    public string ShaBase { get; private set; } = "";
    public EstadoDaReservaDeWorktree Estado { get; private set; } = EstadoDaReservaDeWorktree.Reservada;
    // Host que reservou: os limites são por host, e só ele limpa o diretório físico.
    public string MaquinaDoWorker { get; private set; } = "";
    // Motivo do estado final quando ele exige um (preservada, falhou).
    public string? Motivo { get; private set; }
    public DateTimeOffset CriadaEm { get; private set; }
    public DateTimeOffset? ProntaEm { get; private set; }
    public DateTimeOffset? LiberadaEm { get; private set; }
    public DateTimeOffset? EncerradaEm { get; private set; }

    // Ativa: a worktree é desta execução (caminho, branch e alias exclusivos). OcupaCapacidade: conta nos limites de
    // escrita; uma reserva aguardando a decisão humana é ativa sem ocupar capacidade.
    public bool Ativa => Estado is EstadoDaReservaDeWorktree.Reservada or EstadoDaReservaDeWorktree.Pronta
        or EstadoDaReservaDeWorktree.AguardandoDecisao;
    public bool OcupaCapacidade => Estado is EstadoDaReservaDeWorktree.Reservada or EstadoDaReservaDeWorktree.Pronta;
    public bool Final => Transicoes[Estado].Length == 0;

    public static bool PodeTransitar(EstadoDaReservaDeWorktree de, EstadoDaReservaDeWorktree para) =>
        Transicoes[de].Contains(para);

    // A worktree existe, foi validada (caminho real, raiz Git, diretório Git comum, branch) e pode receber o agente.
    public void MarcarPronta(DateTimeOffset quando)
    {
        Transitar(EstadoDaReservaDeWorktree.Pronta);
        ProntaEm = quando;
    }

    // Gate humano de um Roteiro (#308): libera o slot de escrita e mantém a worktree da execução.
    public void AguardarDecisao() => Transitar(EstadoDaReservaDeWorktree.AguardandoDecisao);

    // Correção pedida no gate: a reserva volta a ocupar capacidade, decidido sob a trava de capacidade.
    public void Readquirir()
    {
        if (Estado != EstadoDaReservaDeWorktree.AguardandoDecisao)
            throw new TransicaoInvalidaException("reserva de worktree", Estado.ToString(),
                EstadoDaReservaDeWorktree.Pronta.ToString());
        Transitar(EstadoDaReservaDeWorktree.Pronta);
    }

    // A execução terminou: a reserva deixa de ocupar capacidade; o diretório espera a limpeza física.
    public void Liberar(DateTimeOffset quando)
    {
        Transitar(EstadoDaReservaDeWorktree.Liberada);
        LiberadaEm = quando;
    }

    public void MarcarRemovida(DateTimeOffset quando)
    {
        Transitar(EstadoDaReservaDeWorktree.Removida);
        EncerradaEm = quando;
    }

    // Alterações, commits além da base ou estado que impede remover sem --force: o diretório e a branch ficam.
    public void Preservar(string motivo, DateTimeOffset quando)
    {
        Transitar(EstadoDaReservaDeWorktree.Preservada, Execucao.TextoCortado(motivo, Execucao.TamanhoMaximoDoTexto,
            nameof(motivo)));
        EncerradaEm = quando;
    }

    // Criação ou validação recusada: o diretório (se existir) nunca é reutilizado nem apagado automaticamente.
    public void Falhar(string motivo, DateTimeOffset quando)
    {
        Transitar(EstadoDaReservaDeWorktree.Falhou, Execucao.TextoCortado(motivo, Execucao.TamanhoMaximoDoTexto,
            nameof(motivo)));
        LiberadaEm ??= quando;
        EncerradaEm = quando;
    }

    private void Transitar(EstadoDaReservaDeWorktree para, string? motivo = null)
    {
        if (!PodeTransitar(Estado, para)) throw new TransicaoInvalidaException("reserva de worktree", Estado.ToString(),
            para.ToString());
        Estado = para;
        Motivo = motivo;
    }

    private static string Absoluto(string caminho, string parametro)
    {
        var valor = Execucao.TextoObrigatorio(caminho, TamanhoMaximoDoCaminho, parametro);
        if (!Path.IsPathFullyQualified(valor)) throw new ArgumentException("Caminho precisa ser absoluto.", parametro);
        return valor;
    }
}
