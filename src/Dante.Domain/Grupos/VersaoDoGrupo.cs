using Dante.Domain.Comum;

namespace Dante.Domain.Grupos;

// Configuração congelada de um grupo (AD-69). Imutável: nenhuma propriedade muda depois de criada, e o banco recusa
// UPDATE e DELETE dela e dos participantes. Editar o grupo é criar a versão seguinte; uma versão só é usada por
// execuções depois de ativada por decisão humana vinculada ao hash dela, e a execução guarda o Id da versão, então
// uma edição ou ativação posterior nunca alcança o que já começou.
public sealed class VersaoDoGrupo : EntidadeBase
{
    public const int TamanhoDoHash = 64;

    private readonly List<ParticipanteDaVersaoDoGrupo> participantes = [];

    private VersaoDoGrupo()
    {
    }

    internal VersaoDoGrupo(GrupoDeAgentes grupo, int numero, ConfiguracaoDoGrupo configuracao, long idDoAutor,
        DateTimeOffset criadaEm)
    {
        ArgumentNullException.ThrowIfNull(grupo);
        ArgumentNullException.ThrowIfNull(configuracao);
        if (numero < 1) throw new ArgumentOutOfRangeException(nameof(numero), "Número da versão deve ser positivo.");
        IdGrupo = grupo.Id;
        Numero = numero;
        Modo = configuracao.Modo;
        participantes.AddRange(configuracao.Participantes.Select(item => new ParticipanteDaVersaoDoGrupo(Id, item)));
        Roteiro = configuracao.Roteiro;
        PapelDoCoordenador = configuracao.PapelDoCoordenador;
        RodadasDeRevisaoMaximas = configuracao.Politica.RodadasDeRevisaoMaximas;
        ParalelismoMaximo = configuracao.Politica.ParalelismoMaximo;
        RepositoriosPermitidos = configuracao.Politica.RepositoriosPermitidos!;
        DuracaoMaximaEmMinutos = configuracao.Politica.DuracaoMaximaEmMinutos;
        HashDaConfiguracao = configuracao.Hash();
        IdDoAutor = idDoAutor;
        CriadaEm = criadaEm;
    }

    public Guid IdGrupo { get; private set; }
    public int Numero { get; private set; }
    public ModoDeCoordenacao Modo { get; private set; }
    public IReadOnlyList<ParticipanteDaVersaoDoGrupo> Participantes => participantes.AsReadOnly();
    public IReadOnlyList<PassoDoRoteiro> Roteiro { get; private set; } = [];
    // Modo Coordenador (AD-75): o papel do participante que coordena; nulo no Roteiro.
    public string? PapelDoCoordenador { get; private set; }
    public int RodadasDeRevisaoMaximas { get; private set; }
    public int ParalelismoMaximo { get; private set; }
    public IReadOnlyList<string> RepositoriosPermitidos { get; private set; } = [];
    public int? DuracaoMaximaEmMinutos { get; private set; }
    public string HashDaConfiguracao { get; private set; } = "";
    // Pessoa (id no canal) que criou esta versão.
    public long IdDoAutor { get; private set; }
    public DateTimeOffset CriadaEm { get; private set; }

    // Reconstrói a configuração gravada; o hash dela é o hash guardado enquanto a versão não for adulterada. Uma
    // versão cujo conteúdo gravado não passa mais na validação também não é íntegra.
    public ConfiguracaoDoGrupo? Configuracao
    {
        get
        {
            try
            {
                return new ConfiguracaoDoGrupo(Modo, participantes.Select(item => item.Participante), Roteiro,
                    new PoliticaDoGrupo(RodadasDeRevisaoMaximas, ParalelismoMaximo, RepositoriosPermitidos,
                        DuracaoMaximaEmMinutos), PapelDoCoordenador);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    public bool Integra => Configuracao?.Hash() == HashDaConfiguracao;
}
