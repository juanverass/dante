using Dante.Domain.Comum;

namespace Dante.Domain.Agentes;

// Configuração congelada de um agente (AD-68). Imutável: nenhuma propriedade muda depois de criada, e o banco recusa
// UPDATE e DELETE. Editar o agente é criar a versão seguinte; sessões e execuções guardam o Id desta versão, então uma
// edição nunca alcança o que já está rodando.
public sealed class VersaoDoAgente : EntidadeBase
{
    public const int TamanhoDoHash = 64;

    private VersaoDoAgente()
    {
    }

    internal VersaoDoAgente(Agente agente, int numero, ConfiguracaoDoAgente configuracao, long idDoAutor,
        DateTimeOffset criadaEm)
    {
        ArgumentNullException.ThrowIfNull(agente);
        ArgumentNullException.ThrowIfNull(configuracao);
        if (numero < 1) throw new ArgumentOutOfRangeException(nameof(numero), "Número da versão deve ser positivo.");
        IdAgente = agente.Id;
        Numero = numero;
        Driver = configuracao.Driver;
        Modelo = configuracao.Modelo;
        Esforco = configuracao.Esforco;
        Instrucoes = configuracao.Instrucoes;
        Capacidades = configuracao.Capacidades;
        PerfilPadrao = configuracao.PerfilPadrao;
        PerfilMaximo = configuracao.PerfilMaximo;
        PodeEscrever = configuracao.PodeEscrever;
        Ferramentas = configuracao.Ferramentas;
        Repositorios = configuracao.Repositorios;
        ExecucoesSimultaneas = configuracao.Limites.ExecucoesSimultaneas;
        TokensPorExecucao = configuracao.Limites.TokensPorExecucao;
        TurnosPorEtapa = configuracao.Limites.TurnosPorEtapa;
        MinutosPorEtapa = configuracao.Limites.MinutosPorEtapa;
        HashDaConfiguracao = configuracao.Hash();
        IdDoAutor = idDoAutor;
        CriadaEm = criadaEm;
    }

    public Guid IdAgente { get; private set; }
    public int Numero { get; private set; }
    public AgentKind Driver { get; private set; }
    public string? Modelo { get; private set; }
    public string? Esforco { get; private set; }
    public string? Instrucoes { get; private set; }
    public IReadOnlyList<CapacidadeDoAgente> Capacidades { get; private set; } = [];
    public AgentPermissionProfile PerfilPadrao { get; private set; }
    public AgentPermissionProfile PerfilMaximo { get; private set; }
    public bool PodeEscrever { get; private set; }
    public IReadOnlyList<FerramentaDoDante> Ferramentas { get; private set; } = [];
    public IReadOnlyList<string> Repositorios { get; private set; } = [];
    public int? ExecucoesSimultaneas { get; private set; }
    public long? TokensPorExecucao { get; private set; }
    public int? TurnosPorEtapa { get; private set; }
    public int? MinutosPorEtapa { get; private set; }
    public string HashDaConfiguracao { get; private set; } = "";
    // Pessoa (id no canal) que criou esta versão.
    public long IdDoAutor { get; private set; }
    public DateTimeOffset CriadaEm { get; private set; }

    // Reconstrói a configuração gravada; o hash dela é o hash guardado enquanto a versão não for adulterada.
    public ConfiguracaoDoAgente Configuracao => new(Driver, PerfilPadrao, PerfilMaximo, PodeEscrever, Modelo, Esforco,
        Instrucoes, Capacidades, Ferramentas, Repositorios,
        new LimitesDoAgente(ExecucoesSimultaneas, TokensPorExecucao, TurnosPorEtapa, MinutosPorEtapa));

    public bool Integra => Configuracao.Hash() == HashDaConfiguracao;
}
