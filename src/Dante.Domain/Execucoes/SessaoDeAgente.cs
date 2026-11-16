using Dante.Domain.Agentes;
using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// Metadados persistidos de uma sessão de agente. O processo e a conversa upstream continuam só em memória (AD-20);
// este registro sobrevive ao reinício e guarda o código S…, que vem de sequência do banco e não se repete.
public sealed class SessaoDeAgente : EntidadeBase
{
    public const int TamanhoMaximoDoUpstream = 200;
    public const int TamanhoMaximoDaUnidade = 100;

    private static readonly IReadOnlyDictionary<EstadoDaSessaoDeAgente, EstadoDaSessaoDeAgente[]> Transicoes =
        new Dictionary<EstadoDaSessaoDeAgente, EstadoDaSessaoDeAgente[]>
        {
            [EstadoDaSessaoDeAgente.Iniciando] =
            [
                EstadoDaSessaoDeAgente.Ativa, EstadoDaSessaoDeAgente.Encerrada, EstadoDaSessaoDeAgente.Falhou,
                EstadoDaSessaoDeAgente.Interrompida
            ],
            [EstadoDaSessaoDeAgente.Ativa] =
                [EstadoDaSessaoDeAgente.Encerrada, EstadoDaSessaoDeAgente.Falhou, EstadoDaSessaoDeAgente.Interrompida],
            [EstadoDaSessaoDeAgente.Encerrada] = [],
            [EstadoDaSessaoDeAgente.Falhou] = [],
            [EstadoDaSessaoDeAgente.Interrompida] = []
        };

    private SessaoDeAgente()
    {
    }

    public SessaoDeAgente(long numero, AgentKind agente, string canal, long idDoDono, string contexto,
        AgentPermissionProfile perfil, DateTimeOffset criadaEm, string? modelo = null, string? esforco = null,
        Guid? idExecucao = null, Guid? bootDoWorker = null)
    {
        if (numero < 1) throw new ArgumentOutOfRangeException(nameof(numero), "Número da sessão deve ser positivo.");
        if (!Enum.IsDefined(agente)) throw new ArgumentOutOfRangeException(nameof(agente), "Agente inválido.");
        if (!Enum.IsDefined(perfil)) throw new ArgumentOutOfRangeException(nameof(perfil), "Perfil inválido.");
        if (idExecucao == Guid.Empty) throw new ArgumentException("Execução inválida.", nameof(idExecucao));
        if (bootDoWorker == Guid.Empty) throw new ArgumentException("Boot inválido.", nameof(bootDoWorker));
        Numero = numero;
        Agente = agente;
        Canal = Execucao.TextoObrigatorio(canal, Execucao.TamanhoMaximoDoCanal, nameof(canal));
        IdDoDono = idDoDono;
        Contexto = Execucao.TextoObrigatorio(contexto, Execucao.TamanhoMaximoDoContexto, nameof(contexto));
        Perfil = perfil;
        Modelo = Execucao.TextoOpcional(modelo, EtapaDaExecucao.TamanhoMaximoDoModelo);
        Esforco = Execucao.TextoOpcional(esforco, EtapaDaExecucao.TamanhoMaximoDoModelo);
        IdExecucao = idExecucao;
        BootDoWorker = bootDoWorker;
        CriadaEm = criadaEm;
    }

    public long Numero { get; private set; }
    public AgentKind Agente { get; private set; }
    public EstadoDaSessaoDeAgente Estado { get; private set; } = EstadoDaSessaoDeAgente.Iniciando;
    public string Canal { get; private set; } = "";
    public long IdDoDono { get; private set; }
    // Contexto fixado no início da sessão (AD-20), serializado pela Application.
    public string Contexto { get; private set; } = "";
    public AgentPermissionProfile Perfil { get; private set; }
    public string? Modelo { get; private set; }
    public string? Esforco { get; private set; }
    // Modelo que o agente informou ao iniciar, quando a sessão usa o padrão.
    public string? ModeloInformado { get; private set; }
    // Identificador da conversa upstream; uma limpeza de contexto o substitui.
    public string? IdUpstream { get; private set; }
    // Execução para a qual a sessão foi aberta; nulo na conversa comum.
    public Guid? IdExecucao { get; private set; }
    // Boot do Worker que criou a sessão e unidade de contenção (scope dante-agent-*) do processo dela (AD-67): a
    // recuperação na inicialização só para unidades registradas aqui por um boot anterior.
    public Guid? BootDoWorker { get; private set; }
    public string? UnidadeDeContencao { get; private set; }
    public string? MotivoDoFim { get; private set; }
    public DateTimeOffset CriadaEm { get; private set; }
    public DateTimeOffset? IniciadaEm { get; private set; }
    public DateTimeOffset? EncerradaEm { get; private set; }

    public string Codigo => $"S{Numero:D6}";
    public bool Terminal => Transicoes[Estado].Length == 0;

    public static bool PodeTransitar(EstadoDaSessaoDeAgente de, EstadoDaSessaoDeAgente para) =>
        Transicoes[de].Contains(para);

    public void MarcarIniciada(string? idUpstream, string? modeloInformado, DateTimeOffset quando,
        string? unidadeDeContencao = null)
    {
        if (unidadeDeContencao is not null && (string.IsNullOrWhiteSpace(unidadeDeContencao) ||
                                               unidadeDeContencao.Length > TamanhoMaximoDaUnidade))
            throw new ArgumentException("Unidade de contenção inválida.", nameof(unidadeDeContencao));
        Transitar(EstadoDaSessaoDeAgente.Ativa);
        UnidadeDeContencao = unidadeDeContencao;
        IdUpstream = Execucao.TextoOpcional(idUpstream, TamanhoMaximoDoUpstream);
        ModeloInformado = Execucao.TextoOpcional(modeloInformado, EtapaDaExecucao.TamanhoMaximoDoModelo);
        IniciadaEm = quando;
    }

    public void Encerrar(string? idUpstream, DateTimeOffset quando) =>
        Finalizar(EstadoDaSessaoDeAgente.Encerrada, null, idUpstream, quando);

    public void Falhar(string motivo, string? idUpstream, DateTimeOffset quando) =>
        Finalizar(EstadoDaSessaoDeAgente.Falhou,
            Execucao.TextoCortado(motivo, Execucao.TamanhoMaximoDoTexto, nameof(motivo)), idUpstream, quando);

    // O Worker parou com a sessão viva: o processo morreu com ele e a conversa upstream não é retomada.
    public void Interromper(string motivo, string? idUpstream, DateTimeOffset quando) =>
        Finalizar(EstadoDaSessaoDeAgente.Interrompida,
            Execucao.TextoCortado(motivo, Execucao.TamanhoMaximoDoTexto, nameof(motivo)), idUpstream, quando);

    private void Finalizar(EstadoDaSessaoDeAgente para, string? motivo, string? idUpstream, DateTimeOffset quando)
    {
        Transitar(para);
        MotivoDoFim = motivo;
        if (Execucao.TextoOpcional(idUpstream, TamanhoMaximoDoUpstream) is { } upstream) IdUpstream = upstream;
        EncerradaEm = quando;
    }

    private void Transitar(EstadoDaSessaoDeAgente para)
    {
        if (!PodeTransitar(Estado, para)) throw new TransicaoInvalidaException("sessão", Estado.ToString(), para.ToString());
        Estado = para;
    }
}
