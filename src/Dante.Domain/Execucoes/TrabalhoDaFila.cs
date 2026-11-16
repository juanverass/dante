using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// Trabalho da fila durável (AD-67): outbox gravado na mesma transação da execução/etapa que o originou. Reivindicação,
// lease, heartbeat e encerramento são operações atômicas no banco (FOR UPDATE SKIP LOCKED e CAS por geração), não
// métodos desta classe: um processo que perdeu o lease nunca grava com uma cópia em memória. A geração é o fencing
// token: cresce a cada reivindicação, e toda gravação sob posse confere geração, dono e lease.
// Dados carregam só identificadores; nunca texto do pedido, saída de processo, caminho de arquivo ou segredo.
public sealed class TrabalhoDaFila : EntidadeBase
{
    public const int TamanhoMaximoDaChave = 200;
    public const int TamanhoMaximoDosDados = 2000;
    public const int TamanhoMaximoDoTrabalhador = 200;
    public const int TamanhoMaximoDoErro = 200;
    public const int MaximoDeTentativasPadrao = 5;

    private TrabalhoDaFila()
    {
    }

    // disponivelEm: nulo é "agora" (criadoEm); futuro é timer ou backoff.
    public TrabalhoDaFila(TipoDeTrabalho tipo, string chaveDeDeduplicacao, DateTimeOffset criadoEm,
        Guid? idExecucao = null, Guid? idEtapa = null, string? dados = null, int prioridade = 0,
        DateTimeOffset? disponivelEm = null, int maximoDeTentativas = MaximoDeTentativasPadrao)
    {
        if (!Enum.IsDefined(tipo)) throw new ArgumentOutOfRangeException(nameof(tipo), "Tipo de trabalho inválido.");
        ChaveDeDeduplicacao = Execucao.TextoObrigatorio(chaveDeDeduplicacao, TamanhoMaximoDaChave,
            nameof(chaveDeDeduplicacao));
        if (idExecucao == Guid.Empty) throw new ArgumentException("Execução inválida.", nameof(idExecucao));
        if (idEtapa == Guid.Empty || (idEtapa is not null && idExecucao is null))
            throw new ArgumentException("Etapa só com a sua execução.", nameof(idEtapa));
        if (dados is { Length: > TamanhoMaximoDosDados })
            throw new ArgumentException("Dados do trabalho excedem o tamanho máximo.", nameof(dados));
        if (maximoDeTentativas < 1)
            throw new ArgumentOutOfRangeException(nameof(maximoDeTentativas), "Ao menos uma tentativa.");
        Tipo = tipo;
        IdExecucao = idExecucao;
        IdEtapa = idEtapa;
        Dados = string.IsNullOrWhiteSpace(dados) ? null : dados;
        Prioridade = prioridade;
        CriadoEm = criadoEm;
        DisponivelEm = disponivelEm ?? criadoEm;
        MaximoDeTentativas = maximoDeTentativas;
    }

    public TipoDeTrabalho Tipo { get; private set; }
    public EstadoDoTrabalho Estado { get; private set; } = EstadoDoTrabalho.Pendente;
    public Guid? IdExecucao { get; private set; }
    public Guid? IdEtapa { get; private set; }
    // JSON pequeno, só com identificadores.
    public string? Dados { get; private set; }
    // Única: enfileirar duas vezes a mesma chave produz no máximo um trabalho e uma execução lógica.
    public string ChaveDeDeduplicacao { get; private set; } = "";
    // Menor primeiro.
    public int Prioridade { get; private set; }
    public DateTimeOffset DisponivelEm { get; private set; }
    public int Tentativas { get; private set; }
    public int MaximoDeTentativas { get; private set; }
    // Quem reivindicou: host:pid e o boot do processo; nulos quando pendente.
    public string? Trabalhador { get; private set; }
    public Guid? BootDoTrabalhador { get; private set; }
    public DateTimeOffset? LeaseAte { get; private set; }
    public long Geracao { get; private set; }
    // Só o tipo do erro ou um motivo curto, nunca conteúdo.
    public string? UltimoErro { get; private set; }
    public DateTimeOffset CriadoEm { get; private set; }
    public DateTimeOffset? EncerradoEm { get; private set; }

    public bool Terminal => Estado is EstadoDoTrabalho.Concluido or EstadoDoTrabalho.Falhou or EstadoDoTrabalho.Descartado;

    // Trabalho que avança uma etapa de uma execução.
    public static TrabalhoDaFila AvancarExecucao(Execucao execucao, EtapaDaExecucao etapa, string chaveDeDeduplicacao,
        DateTimeOffset criadoEm, int prioridade = 0, DateTimeOffset? disponivelEm = null)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        ArgumentNullException.ThrowIfNull(etapa);
        if (etapa.IdExecucao != execucao.Id) throw new InvalidOperationException("Etapa de outra execução.");
        if (execucao.Terminal) throw new InvalidOperationException("Execução encerrada não recebe trabalho.");
        return new TrabalhoDaFila(TipoDeTrabalho.AvancarExecucao, chaveDeDeduplicacao, criadoEm, execucao.Id, etapa.Id,
            $"{{\"execucao\":\"{execucao.Codigo}\",\"etapa\":{etapa.Numero},\"tentativa\":{etapa.Tentativa}}}",
            prioridade, disponivelEm);
    }

    // Backoff exponencial de erro transitório do D.A.N.T.E.: base, 2×base, 4×base… até o teto.
    public static TimeSpan Backoff(int tentativas, TimeSpan basico, TimeSpan teto)
    {
        if (tentativas < 1) return TimeSpan.Zero;
        var expoente = Math.Min(tentativas - 1, 20);
        var espera = TimeSpan.FromTicks(basico.Ticks * (1L << expoente));
        return espera > teto || espera < TimeSpan.Zero ? teto : espera;
    }
}
