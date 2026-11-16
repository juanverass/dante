using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// Evento-chave do ledger, de uma execução ou de uma sessão. A sequência é global e crescente, atribuída pelo banco
// ao gravar; consumidores retomam a leitura por ela. Dados carregam só metadados (estados, códigos, contagens):
// nunca texto de mensagem, delta, saída de processo ou descrição de comando.
public sealed class EventoDeOrquestracao : EntidadeBase
{
    public const int TamanhoMaximoDosDados = 4000;

    private EventoDeOrquestracao()
    {
    }

    private EventoDeOrquestracao(TipoDeEventoDeOrquestracao tipo, Guid? idExecucao, Guid? idEtapa,
        Guid? idSessaoDeAgente, string? idTurno, string? dados, DateTimeOffset ocorridoEm)
    {
        if (!Enum.IsDefined(tipo)) throw new ArgumentOutOfRangeException(nameof(tipo), "Tipo de evento inválido.");
        if (dados is { Length: > TamanhoMaximoDosDados })
            throw new ArgumentException("Dados do evento excedem o tamanho máximo.", nameof(dados));
        if (idTurno is { Length: > EtapaDaExecucao.TamanhoMaximoDoTurno })
            throw new ArgumentException("Turno inválido.", nameof(idTurno));
        Tipo = tipo;
        IdExecucao = idExecucao;
        IdEtapa = idEtapa;
        IdSessaoDeAgente = idSessaoDeAgente;
        IdTurno = string.IsNullOrWhiteSpace(idTurno) ? null : idTurno;
        Dados = string.IsNullOrWhiteSpace(dados) ? null : dados;
        OcorridoEm = ocorridoEm;
    }

    public long Sequencia { get; private set; }
    public TipoDeEventoDeOrquestracao Tipo { get; private set; }
    public Guid? IdExecucao { get; private set; }
    public Guid? IdEtapa { get; private set; }
    public Guid? IdSessaoDeAgente { get; private set; }
    public string? IdTurno { get; private set; }
    // JSON pequeno montado pela Application.
    public string? Dados { get; private set; }
    public DateTimeOffset OcorridoEm { get; private set; }

    public static EventoDeOrquestracao DaExecucao(Execucao execucao, TipoDeEventoDeOrquestracao tipo, string? dados,
        DateTimeOffset ocorridoEm, EtapaDaExecucao? etapa = null)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        if (etapa is not null && etapa.IdExecucao != execucao.Id)
            throw new InvalidOperationException("Etapa de outra execução.");
        return new(tipo, execucao.Id, etapa?.Id, etapa?.IdSessaoDeAgente, etapa?.IdTurno, dados, ocorridoEm);
    }

    public static EventoDeOrquestracao DaSessao(SessaoDeAgente sessao, TipoDeEventoDeOrquestracao tipo, string? idTurno,
        string? dados, DateTimeOffset ocorridoEm)
    {
        ArgumentNullException.ThrowIfNull(sessao);
        return new(tipo, sessao.IdExecucao, null, sessao.Id, idTurno, dados, ocorridoEm);
    }
}
