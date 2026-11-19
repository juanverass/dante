using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// Gate humano persistido de um Roteiro (#308, decisão F): a pessoa decide sobre um artefato exato. O pedido guarda o
// hash do snapshot (grupo e participantes) e o hash do artefato revisado (o diff da worktree), e a decisão de aprovar
// ou pedir correção só vale com a confirmação do hash do artefato exibido. Aprovar aceita o resultado e encerra o
// roteiro; não autoriza commit, push, PR, merge, deploy nem nada além das capacidades já concedidas. Uma decisão só:
// depois dela, o pedido é imutável (trigger), e uma segunda decisão é recusada. Sobrevive a reinício porque é linha do
// ledger, não estado de processo.
// Coordenação (AD-75, #310): o mesmo gate decide um plano (DoPlano). O artefato é a forma canônica do plano e o hash
// dele; a rodada é a revisão do plano. A finalidade fica gravada e o significado não se mistura: aprovar um plano
// autoriza só aquele plano somente leitura, e nenhuma decisão deste gate é aprovação do gateway da #311.
public sealed class PedidoDeAprovacao : EntidadeBase
{
    public const int TamanhoDoHash = 64;
    public const int PrefixoMinimoDoHash = 12;
    public const int TamanhoMaximoDoMotivo = Execucao.TamanhoMaximoDoTexto;

    private PedidoDeAprovacao()
    {
    }

    public PedidoDeAprovacao(Execucao execucao, EtapaDaExecucao etapa, int rodada, string hashDoSnapshot,
        string hashDoArtefato, DateTimeOffset criadoEm)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(etapa);
        if (execucao.Tipo != TipoDeExecucao.Roteiro || etapa.IdExecucao != execucao.Id ||
            etapa.Tipo != TipoDeEtapa.AprovacaoHumana)
            throw new InvalidOperationException("Pedido de aprovação só na etapa de aprovação humana de um Roteiro.");
        if (rodada < 0) throw new ArgumentOutOfRangeException(nameof(rodada));
        IdExecucao = execucao.Id;
        IdEtapa = etapa.Id;
        Rodada = rodada;
        HashDoSnapshot = Hash(hashDoSnapshot, nameof(hashDoSnapshot));
        HashDoArtefato = Hash(hashDoArtefato, nameof(hashDoArtefato));
        CriadoEm = criadoEm;
    }

    // Gate do plano de uma Coordenada: o pedido nasce do plano que aguarda decisão, com o hash calculado pelo próprio
    // plano (nunca um hash informado) e o hash do snapshot congelado da execução.
    public static PedidoDeAprovacao DoPlano(Execucao execucao, EtapaDaExecucao etapa, PlanoDaCoordenacao plano,
        CoordenacaoDaExecucao coordenacao, DateTimeOffset criadoEm)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(etapa);
        ArgumentNullException.ThrowIfNull(plano);
        ArgumentNullException.ThrowIfNull(coordenacao);
        if (execucao.Tipo != TipoDeExecucao.Coordenada || etapa.IdExecucao != execucao.Id ||
            etapa.Tipo != TipoDeEtapa.AprovacaoHumana || plano.IdExecucao != execucao.Id ||
            coordenacao.IdExecucao != execucao.Id)
            throw new InvalidOperationException("Pedido de aprovação de plano só na etapa de aprovação da própria Coordenada.");
        if (plano.Estado != EstadoDoPlano.AguardandoAprovacao)
            throw new InvalidOperationException("O plano não aguarda decisão.");
        return new PedidoDeAprovacao
        {
            IdExecucao = execucao.Id,
            IdEtapa = etapa.Id,
            Rodada = plano.Revisao,
            HashDoSnapshot = Hash(coordenacao.HashDoSnapshot, nameof(coordenacao)),
            HashDoArtefato = Hash(plano.Hash, nameof(plano)),
            Finalidade = FinalidadeDaAprovacao.PlanoSomenteLeitura,
            IdPlano = plano.Id,
            CriadoEm = criadoEm
        };
    }

    public FinalidadeDaAprovacao Finalidade { get; private set; } = FinalidadeDaAprovacao.ArtefatoDoRoteiro;
    // O plano decidido (finalidade PlanoSomenteLeitura); nulo no gate do Roteiro.
    public Guid? IdPlano { get; private set; }

    public Guid IdExecucao { get; private set; }
    public Guid IdEtapa { get; private set; }
    public int Rodada { get; private set; }
    public string HashDoSnapshot { get; private set; } = "";
    public string HashDoArtefato { get; private set; } = "";
    public EstadoDoPedidoDeAprovacao Estado { get; private set; } = EstadoDoPedidoDeAprovacao.Pendente;
    // Quem decidiu: canal e pessoa nele (a mesma autoridade de origem da execução).
    public string? CanalDoDecisor { get; private set; }
    public long? IdDoDecisor { get; private set; }
    public DateTimeOffset? DecididoEm { get; private set; }
    // Instrução de correção ou motivo da recusa, já redigido pela Application.
    public string? Motivo { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }

    public bool Pendente => Estado == EstadoDoPedidoDeAprovacao.Pendente;

    public void Aprovar(string canal, long idDoDecisor, string? hashConfirmado, DateTimeOffset quando)
    {
        ConferirHash(hashConfirmado);
        Decidir(EstadoDoPedidoDeAprovacao.Aprovado, canal, idDoDecisor, null, quando);
    }

    // A instrução vai ao corretor: obrigatória.
    public void SolicitarCorrecao(string canal, long idDoDecisor, string? hashConfirmado, string instrucao,
        DateTimeOffset quando)
    {
        ConferirHash(hashConfirmado);
        if (string.IsNullOrWhiteSpace(instrucao)) throw new ArgumentException("Diga o que corrigir.", nameof(instrucao));
        Decidir(EstadoDoPedidoDeAprovacao.CorrecaoSolicitada, canal, idDoDecisor, instrucao, quando);
    }

    // Recusar encerra sem aceitar nada; não depende do artefato exibido.
    public void Recusar(string canal, long idDoDecisor, string? motivo, DateTimeOffset quando) =>
        Decidir(EstadoDoPedidoDeAprovacao.Recusado, canal, idDoDecisor, motivo, quando);

    // O artefato presente na worktree não é mais o revisado (hash atual diferente do deste pedido): o dono pede uma
    // nova revisão, e este pedido deixa de aceitar decisão. Recusado se o hash atual for o mesmo (nada a revisar).
    public void Substituir(string canal, long idDoDecisor, string hashAtual, DateTimeOffset quando)
    {
        // O plano é imutável: outra versão é outra revisão, com pedido próprio.
        if (Finalidade != FinalidadeDaAprovacao.ArtefatoDoRoteiro)
            throw new InvalidOperationException("Um plano não é substituído no gate; uma revisão nova tem pedido próprio.");
        if (hashAtual is not { Length: TamanhoDoHash }) throw new ArgumentException("Hash inválido.", nameof(hashAtual));
        if (hashAtual == HashDoArtefato)
            throw new InvalidOperationException("O conteúdo da worktree ainda é o revisado; decida sobre ele.");
        Decidir(EstadoDoPedidoDeAprovacao.Substituido, canal, idDoDecisor,
            $"Artefato alterado depois da revisão (atual {hashAtual[..PrefixoMinimoDoHash]}); nova revisão pedida.", quando);
    }

    public void Cancelar(string? motivo, DateTimeOffset quando)
    {
        if (!Pendente) throw Decidido();
        Estado = EstadoDoPedidoDeAprovacao.Cancelado;
        Motivo = Execucao.TextoOpcional(motivo, TamanhoMaximoDoMotivo);
        DecididoEm = quando;
    }

    public static bool ConfereHash(string hash, string? confirmado)
    {
        var texto = confirmado?.Trim().ToLowerInvariant();
        return texto is { Length: >= PrefixoMinimoDoHash } && texto.Length <= hash.Length &&
            hash.StartsWith(texto, StringComparison.Ordinal);
    }

    private void ConferirHash(string? confirmado)
    {
        if (!ConfereHash(HashDoArtefato, confirmado))
            throw new InvalidOperationException(
                "O hash confirmado não é o do resultado revisado; confira o hash exibido (ao menos 12 caracteres).");
    }

    private void Decidir(EstadoDoPedidoDeAprovacao estado, string canal, long idDoDecisor, string? motivo,
        DateTimeOffset quando)
    {
        if (!Pendente) throw Decidido();
        if (string.IsNullOrWhiteSpace(canal)) throw new ArgumentException("Canal obrigatório.", nameof(canal));
        Estado = estado;
        CanalDoDecisor = canal;
        IdDoDecisor = idDoDecisor;
        Motivo = Execucao.TextoOpcional(motivo, TamanhoMaximoDoMotivo);
        DecididoEm = quando;
    }

    private InvalidOperationException Decidido() =>
        new($"Este pedido de aprovação já foi decidido ({Estado}).");

    private static string Hash(string valor, string parametro) =>
        valor is { Length: TamanhoDoHash } && valor.All(Uri.IsHexDigit) && valor == valor.ToLowerInvariant()
            ? valor : throw new ArgumentException("Hash inválido.", parametro);
}
