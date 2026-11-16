using Dante.Domain.Agentes;
using Dante.Domain.Comum;

namespace Dante.Domain.Execucoes;

// Metadados persistidos de uma sessão de agente. O processo e a conversa upstream continuam só em memória (AD-20);
// este registro sobrevive ao reinício e guarda o código S…, que vem de sequência do banco e não se repete.
public sealed class SessaoDeAgente : EntidadeBase
{
    public const int TamanhoMaximoDoUpstream = 200;

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
        Guid? idExecucao = null, string? handleDoAgente = null, string? hashDaConfiguracaoDoAgente = null,
        Guid? idVersaoDoAgente = null)
    {
        if (numero < 1) throw new ArgumentOutOfRangeException(nameof(numero), "Número da sessão deve ser positivo.");
        if (!Enum.IsDefined(agente)) throw new ArgumentOutOfRangeException(nameof(agente), "Agente inválido.");
        if (!Enum.IsDefined(perfil)) throw new ArgumentOutOfRangeException(nameof(perfil), "Perfil inválido.");
        if (idExecucao == Guid.Empty) throw new ArgumentException("Execução inválida.", nameof(idExecucao));
        if (idVersaoDoAgente == Guid.Empty) throw new ArgumentException("Versão do agente inválida.", nameof(idVersaoDoAgente));
        // Agente da sessão (AD-68): embutido (claude/codex, sem versão persistida) ou registrado (com a versão exata).
        if (handleDoAgente is not null && (handleDoAgente.Length is 0 or > Dante.Domain.Agentes.Agente.TamanhoMaximoDoHandle))
            throw new ArgumentException("Handle inválido.", nameof(handleDoAgente));
        if (hashDaConfiguracaoDoAgente is not null && hashDaConfiguracaoDoAgente.Length != VersaoDoAgente.TamanhoDoHash)
            throw new ArgumentException("Hash da configuração inválido.", nameof(hashDaConfiguracaoDoAgente));
        if (idVersaoDoAgente is not null && handleDoAgente is null)
            throw new ArgumentException("Versão sem handle do agente.", nameof(handleDoAgente));
        HandleDoAgente = handleDoAgente;
        HashDaConfiguracaoDoAgente = hashDaConfiguracaoDoAgente;
        IdVersaoDoAgente = idVersaoDoAgente;
        Numero = numero;
        Agente = agente;
        Canal = Execucao.TextoObrigatorio(canal, Execucao.TamanhoMaximoDoCanal, nameof(canal));
        IdDoDono = idDoDono;
        Contexto = Execucao.TextoObrigatorio(contexto, Execucao.TamanhoMaximoDoContexto, nameof(contexto));
        Perfil = perfil;
        Modelo = Execucao.TextoOpcional(modelo, EtapaDaExecucao.TamanhoMaximoDoModelo);
        Esforco = Execucao.TextoOpcional(esforco, EtapaDaExecucao.TamanhoMaximoDoModelo);
        IdExecucao = idExecucao;
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
    // Agente congelado no início (AD-68): handle, hash da configuração e, se registrado, a versão exata.
    public string? HandleDoAgente { get; private set; }
    public string? HashDaConfiguracaoDoAgente { get; private set; }
    public Guid? IdVersaoDoAgente { get; private set; }
    public string? MotivoDoFim { get; private set; }
    public DateTimeOffset CriadaEm { get; private set; }
    public DateTimeOffset? IniciadaEm { get; private set; }
    public DateTimeOffset? EncerradaEm { get; private set; }

    public string Codigo => $"S{Numero:D6}";
    public bool Terminal => Transicoes[Estado].Length == 0;

    public static bool PodeTransitar(EstadoDaSessaoDeAgente de, EstadoDaSessaoDeAgente para) =>
        Transicoes[de].Contains(para);

    public void MarcarIniciada(string? idUpstream, string? modeloInformado, DateTimeOffset quando)
    {
        Transitar(EstadoDaSessaoDeAgente.Ativa);
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
