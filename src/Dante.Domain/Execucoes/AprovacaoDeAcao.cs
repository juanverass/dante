using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// Estado de uma aprovação de ação protegida (AD-72, #311).
// Pendente → Aprovada | Recusada | Expirada | Cancelada
// Aprovada → EmExecucao | Expirada | Cancelada
// EmExecucao → Executada | Falhou | Incerto
// As demais são terminais. Cancelada: revogada antes de executar (execução cancelada, concessão revogada, agente
// desativado, payload atual diferente do aprovado). Falhou: a ação comprovadamente não teve efeito. Incerto: a execução
// foi interrompida sem saber se teve efeito; nunca é repetida automaticamente.
public enum EstadoDaAprovacaoDeAcao
{
    Pendente,
    Aprovada,
    Recusada,
    Expirada,
    Cancelada,
    EmExecucao,
    Executada,
    Falhou,
    Incerto
}

// Só UmaVez nesta fase (AD-72): uma aprovação autoriza uma execução de um payload; nunca vale para a execução inteira,
// para outra chamada nem de forma global ou permanente.
public enum EscopoDaAprovacao
{
    UmaVez
}

// Resultado de tentar começar a execução de uma ação aprovada.
public enum InicioDaExecucaoDaAcao
{
    Iniciada,
    Expirada,
    PayloadAlterado
}

// Aprovação de ação protegida do gateway (AD-72, #311). Entidade própria, separada do gate de etapa (PedidoDeAprovacao):
// o gate decide um artefato de uma etapa AprovacaoHumana e não autoriza ação; esta autoriza exatamente uma execução do
// payload canônico cujo hash a pessoa confirmou. O payload e o hash são calculados pelo servidor (PayloadDaAcao) e nunca
// mudam; só o dono da execução raiz decide, uma vez, antes de expirar; a execução começa uma vez (estado + xmin: duas
// tentativas concorrentes não passam as duas) e só se o payload remontado com os fatos atuais tiver o mesmo hash.
// O banco confere de novo: hash = SHA-256 do payload, payload vinculado às colunas, ação protegida do catálogo, dono da
// raiz, transições, decisor = dono, prazos e imutabilidade (trigger).
public sealed class AprovacaoDeAcao : EntidadeBase
{
    public const int TamanhoDoHash = PayloadDaAcao.TamanhoDoHash;
    public const int TamanhoMaximoDoMotivo = Execucao.TamanhoMaximoDoTexto;
    public const int TamanhoMaximoDoResultado = Execucao.TamanhoMaximoDoTexto;
    public static readonly TimeSpan ValidadePadrao = TimeSpan.FromHours(24);

    private static readonly Dictionary<EstadoDaAprovacaoDeAcao, EstadoDaAprovacaoDeAcao[]> Transicoes = new()
    {
        [EstadoDaAprovacaoDeAcao.Pendente] =
        [
            EstadoDaAprovacaoDeAcao.Aprovada, EstadoDaAprovacaoDeAcao.Recusada, EstadoDaAprovacaoDeAcao.Expirada,
            EstadoDaAprovacaoDeAcao.Cancelada
        ],
        [EstadoDaAprovacaoDeAcao.Aprovada] =
            [EstadoDaAprovacaoDeAcao.EmExecucao, EstadoDaAprovacaoDeAcao.Expirada, EstadoDaAprovacaoDeAcao.Cancelada],
        [EstadoDaAprovacaoDeAcao.EmExecucao] =
            [EstadoDaAprovacaoDeAcao.Executada, EstadoDaAprovacaoDeAcao.Falhou, EstadoDaAprovacaoDeAcao.Incerto],
        [EstadoDaAprovacaoDeAcao.Recusada] = [],
        [EstadoDaAprovacaoDeAcao.Expirada] = [],
        [EstadoDaAprovacaoDeAcao.Cancelada] = [],
        [EstadoDaAprovacaoDeAcao.Executada] = [],
        [EstadoDaAprovacaoDeAcao.Falhou] = [],
        [EstadoDaAprovacaoDeAcao.Incerto] = []
    };

    private AprovacaoDeAcao()
    {
    }

    // Pedido de aprovação de um payload montado pelo servidor. A execução é a do pedido; a raiz dela define o dono (a
    // única pessoa que decide). A etapa, quando houver, é da própria execução. Um solicitante dono é o dono da raiz.
    public static AprovacaoDeAcao Solicitar(PayloadDaAcao payload, Execucao execucao, Execucao raiz,
        EtapaDaExecucao? etapa, DateTimeOffset criadaEm, TimeSpan? validade = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(raiz);
        if (!payload.Definicao.Protegida) throw new InvalidOperationException("A ação não exige aprovação.");
        if (payload.IdExecucao != execucao.Id) throw new InvalidOperationException("O payload é de outra execução.");
        if (execucao.IdExecucaoRaiz != raiz.Id || !raiz.Raiz)
            throw new InvalidOperationException("A raiz informada não é a da execução.");
        if (payload.IdEtapa != etapa?.Id || (etapa is not null && etapa.IdExecucao != execucao.Id))
            throw new InvalidOperationException("A etapa do payload não é uma etapa desta execução.");
        if (payload.Solicitante.EhDono &&
            (payload.Solicitante.Canal != raiz.Canal || payload.Solicitante.IdDoUsuario != raiz.IdDoUsuarioNoCanal))
            throw new InvalidOperationException("Só o dono da execução raiz pede uma ação em nome próprio.");
        var prazo = validade ?? ValidadePadrao;
        if (prazo <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(validade), "Validade precisa ser positiva.");
        return new AprovacaoDeAcao
        {
            IdExecucao = execucao.Id,
            IdExecucaoRaiz = raiz.Id,
            IdEtapa = etapa?.Id,
            Acao = payload.Definicao.Nome,
            Risco = payload.Definicao.Risco,
            Escopo = EscopoDaAprovacao.UmaVez,
            Alvo = payload.Alvo,
            IdVersaoDoSolicitante = payload.Solicitante.IdVersaoDoAgente,
            HashDaVersaoDoSolicitante = payload.Solicitante.HashDaVersaoDoAgente,
            CanalDoDono = raiz.Canal,
            IdDoDono = raiz.IdDoUsuarioNoCanal,
            Payload = payload.Conteudo,
            Hash = payload.Hash,
            CriadaEm = criadaEm,
            ExpiraEm = criadaEm + prazo
        };
    }

    public Guid IdExecucao { get; private set; }
    public Guid IdExecucaoRaiz { get; private set; }
    public Guid? IdEtapa { get; private set; }
    public string Acao { get; private set; } = "";
    public ClasseDeRisco Risco { get; private set; }
    public EscopoDaAprovacao Escopo { get; private set; }
    public string? Alvo { get; private set; }
    // Solicitante agente (versão congelada); nulos quando o solicitante é o dono.
    public Guid? IdVersaoDoSolicitante { get; private set; }
    public string? HashDaVersaoDoSolicitante { get; private set; }
    // Dono da execução raiz no momento do pedido: a única pessoa que decide.
    public string CanalDoDono { get; private set; } = "";
    public long IdDoDono { get; private set; }
    // Forma canônica (texto, não jsonb: o hash é dos bytes exatos).
    public string Payload { get; private set; } = "";
    public string Hash { get; private set; } = "";
    public EstadoDaAprovacaoDeAcao Estado { get; private set; } = EstadoDaAprovacaoDeAcao.Pendente;
    public DateTimeOffset CriadaEm { get; private set; }
    public DateTimeOffset ExpiraEm { get; private set; }
    public string? CanalDoDecisor { get; private set; }
    public long? IdDoDecisor { get; private set; }
    public DateTimeOffset? DecididaEm { get; private set; }
    // Motivo da recusa, do cancelamento, da falha ou da incerteza, já redigido pela Application.
    public string? Motivo { get; private set; }
    public DateTimeOffset? IniciadaEm { get; private set; }
    public DateTimeOffset? EncerradaEm { get; private set; }
    // Resultado observado (ex.: SHA do commit, ou o estado real do repositório num Incerto), já redigido.
    public string? Resultado { get; private set; }

    public bool Terminal => Transicoes[Estado].Length == 0;

    public static bool PodeTransitar(EstadoDaAprovacaoDeAcao de, EstadoDaAprovacaoDeAcao para) =>
        Transicoes[de].Contains(para);

    // Aprovar exige o hash exibido (ao menos 12 caracteres do início) e o dono da raiz, antes de expirar.
    public void Aprovar(string canal, long idDoDecisor, string? hashConfirmado, DateTimeOffset quando)
    {
        Exigir(EstadoDaAprovacaoDeAcao.Pendente, EstadoDaAprovacaoDeAcao.Aprovada);
        if (quando >= ExpiraEm) throw new InvalidOperationException("O pedido expirou; peça a ação de novo.");
        ConferirDono(canal, idDoDecisor);
        if (!PedidoDeAprovacao.ConfereHash(Hash, hashConfirmado))
            throw new InvalidOperationException(
                "O hash confirmado não é o da ação pedida; confira o hash exibido (ao menos 12 caracteres).");
        Decidir(EstadoDaAprovacaoDeAcao.Aprovada, canal, idDoDecisor, null, quando);
    }

    // Recusar não depende do hash nem do prazo: nada é autorizado.
    public void Recusar(string canal, long idDoDecisor, string? motivo, DateTimeOffset quando)
    {
        Exigir(EstadoDaAprovacaoDeAcao.Pendente, EstadoDaAprovacaoDeAcao.Recusada);
        ConferirDono(canal, idDoDecisor);
        Decidir(EstadoDaAprovacaoDeAcao.Recusada, canal, idDoDecisor, motivo, quando);
    }

    public void Expirar(DateTimeOffset quando)
    {
        Exigir(Estado, EstadoDaAprovacaoDeAcao.Expirada);
        if (quando < ExpiraEm) throw new InvalidOperationException("O pedido ainda não expirou.");
        Encerrar(EstadoDaAprovacaoDeAcao.Expirada, "Prazo da aprovação vencido.", quando);
    }

    // Revogação antes de executar: o motivo é obrigatório e fica na auditoria.
    public void Cancelar(string motivo, DateTimeOffset quando)
    {
        if (string.IsNullOrWhiteSpace(motivo)) throw new ArgumentException("Diga por que a ação foi cancelada.", nameof(motivo));
        Exigir(Estado, EstadoDaAprovacaoDeAcao.Cancelada);
        Encerrar(EstadoDaAprovacaoDeAcao.Cancelada, motivo, quando);
    }

    // Começa a execução única. O payload atual é remontado por quem executa com os fatos de agora (versão do agente,
    // capacidades, conteúdo do artefato): hash diferente cancela a aprovação; prazo vencido a expira. Persistir a
    // transição para EmExecucao é o CAS: com o xmin da linha, só uma de duas tentativas concorrentes grava.
    public InicioDaExecucaoDaAcao IniciarExecucao(PayloadDaAcao atual, DateTimeOffset quando)
    {
        ArgumentNullException.ThrowIfNull(atual);
        Exigir(EstadoDaAprovacaoDeAcao.Aprovada, EstadoDaAprovacaoDeAcao.EmExecucao);
        if (quando >= ExpiraEm)
        {
            Expirar(quando);
            return InicioDaExecucaoDaAcao.Expirada;
        }
        if (!string.Equals(atual.Hash, Hash, StringComparison.Ordinal))
        {
            Encerrar(EstadoDaAprovacaoDeAcao.Cancelada,
                "Payload atual diferente do aprovado (parâmetro, alvo, versão, capacidade ou conteúdo mudou).", quando);
            return InicioDaExecucaoDaAcao.PayloadAlterado;
        }
        Estado = EstadoDaAprovacaoDeAcao.EmExecucao;
        IniciadaEm = quando;
        return InicioDaExecucaoDaAcao.Iniciada;
    }

    public void Concluir(string? resultado, DateTimeOffset quando)
    {
        Exigir(EstadoDaAprovacaoDeAcao.EmExecucao, EstadoDaAprovacaoDeAcao.Executada);
        Resultado = Execucao.TextoOpcional(resultado, TamanhoMaximoDoResultado);
        Encerrar(EstadoDaAprovacaoDeAcao.Executada, null, quando);
    }

    // A ação comprovadamente não teve efeito (ex.: conferência antes de qualquer escrita falhou).
    public void Falhar(string motivo, DateTimeOffset quando)
    {
        if (string.IsNullOrWhiteSpace(motivo)) throw new ArgumentException("Diga por que a ação falhou.", nameof(motivo));
        Exigir(EstadoDaAprovacaoDeAcao.EmExecucao, EstadoDaAprovacaoDeAcao.Falhou);
        Encerrar(EstadoDaAprovacaoDeAcao.Falhou, motivo, quando);
    }

    // Interrompida sem saber se teve efeito (o processo caiu no meio, por exemplo). Terminal: nenhuma repetição
    // automática; o resultado relata o estado real observado, quando houver.
    public void MarcarIncerta(string motivo, string? estadoObservado, DateTimeOffset quando)
    {
        if (string.IsNullOrWhiteSpace(motivo)) throw new ArgumentException("Diga por que o resultado é incerto.", nameof(motivo));
        Exigir(EstadoDaAprovacaoDeAcao.EmExecucao, EstadoDaAprovacaoDeAcao.Incerto);
        Resultado = Execucao.TextoOpcional(estadoObservado, TamanhoMaximoDoResultado);
        Encerrar(EstadoDaAprovacaoDeAcao.Incerto, motivo, quando);
    }

    private void Exigir(EstadoDaAprovacaoDeAcao esperado, EstadoDaAprovacaoDeAcao para)
    {
        if (Estado != esperado || !PodeTransitar(Estado, para))
            throw new TransicaoInvalidaException("aprovação de ação", Estado.ToString(), para.ToString());
    }

    private void ConferirDono(string canal, long idDoDecisor)
    {
        if (string.IsNullOrWhiteSpace(canal)) throw new ArgumentException("Canal obrigatório.", nameof(canal));
        if (canal != CanalDoDono || idDoDecisor != IdDoDono)
            throw new InvalidOperationException("Só o dono da execução decide esta ação.");
    }

    private void Decidir(EstadoDaAprovacaoDeAcao estado, string canal, long idDoDecisor, string? motivo,
        DateTimeOffset quando)
    {
        CanalDoDecisor = canal;
        IdDoDecisor = idDoDecisor;
        DecididaEm = quando;
        if (estado == EstadoDaAprovacaoDeAcao.Aprovada)
        {
            Estado = estado;
            return;
        }
        Encerrar(estado, motivo, quando);
    }

    private void Encerrar(EstadoDaAprovacaoDeAcao estado, string? motivo, DateTimeOffset quando)
    {
        Estado = estado;
        Motivo = Execucao.TextoOpcional(motivo, TamanhoMaximoDoMotivo);
        EncerradaEm = quando;
    }
}
