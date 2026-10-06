using Dante.Domain.Comum;
using Dante.Domain.Conhecimentos;
namespace Dante.Domain.ContextosDeTrabalho;

// Campos operacionais selecionados, sem transcript ou campo de raciocínio do agente.
public sealed record DadosDoContexto(string Objetivo, string Tarefa, string Progresso,
    IReadOnlyList<Guid> IdsDecisoesConfirmadas, IReadOnlyList<string> Referencias,
    IReadOnlyList<string> Pendencias, IReadOnlyList<string> ProximosPassos, string UltimoResultado);
public sealed record AuditoriaDoContexto(int Revisao, Guid IdResponsavel, string Origem, DateTimeOffset AtualizadoEm);
public sealed class ContextoDeTrabalho : EntidadeBase
{
    public const int TamanhoMaximo = 8000;
    private ContextoDeTrabalho() { }
    public ContextoDeTrabalho(Guid idEspaco, Guid? idProjeto, DadosDoContexto dados, Sensibilidade sensibilidade,
        Guid idResponsavel, string origem, DateTimeOffset instante, DateTimeOffset? expiraEm)
    {
        if (idEspaco == Guid.Empty || idProjeto == Guid.Empty) throw new ArgumentException("Escopo inválido.");
        IdEspacoDeConhecimento = idEspaco; IdProjeto = idProjeto;
        Substituir(0, dados, sensibilidade, idResponsavel, origem, instante, expiraEm);
    }
    public Guid IdEspacoDeConhecimento { get; private set; }
    public Guid? IdProjeto { get; private set; }
    public DadosDoContexto Dados { get; private set; } = null!;
    public Sensibilidade Sensibilidade { get; private set; }
    public int Revisao { get; private set; }
    public Guid IdResponsavel { get; private set; }
    public string Origem { get; private set; } = "";
    public DateTimeOffset AtualizadoEm { get; private set; }
    public DateTimeOffset? ExpiraEm { get; private set; }
    public AuditoriaDoContexto? AuditoriaAnterior { get; private set; }
    public bool EstaAtivoEm(DateTimeOffset instante) => ExpiraEm is null || instante < ExpiraEm;
    public void Substituir(int revisaoEsperada, DadosDoContexto dados, Sensibilidade sensibilidade,
        Guid idResponsavel, string origem, DateTimeOffset instante, DateTimeOffset? expiraEm)
    {
        ArgumentNullException.ThrowIfNull(dados);
        if (revisaoEsperada != Revisao) throw new InvalidOperationException("Revisão do contexto desatualizada.");
        if (!Enum.IsDefined(sensibilidade) || idResponsavel == Guid.Empty || string.IsNullOrWhiteSpace(origem) || origem.Length > 200 ||
            instante == default || instante < AtualizadoEm || expiraEm <= instante) throw new ArgumentException("Classificação/origem/validade inválida.");
        var textos = new[] { dados.Objetivo, dados.Tarefa, dados.Progresso, dados.UltimoResultado };
        if (textos.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 2000) ||
            dados.IdsDecisoesConfirmadas.Count > 20 || dados.IdsDecisoesConfirmadas.Any(x => x == Guid.Empty))
            throw new ArgumentException("Campos operacionais excedem os limites.");
        var listas = new[] { dados.Referencias, dados.Pendencias, dados.ProximosPassos };
        if (listas.Any(x => x.Count > 20 || x.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 500)) ||
            textos.Sum(x => x.Length) + listas.Sum(x => x.Sum(t => t.Length)) > TamanhoMaximo)
            throw new ArgumentException("Snapshot excede o tamanho permitido.");
        var novo = dados with { IdsDecisoesConfirmadas = Array.AsReadOnly(dados.IdsDecisoesConfirmadas.Distinct().ToArray()),
            Referencias = Array.AsReadOnly(dados.Referencias.ToArray()), Pendencias = Array.AsReadOnly(dados.Pendencias.ToArray()),
            ProximosPassos = Array.AsReadOnly(dados.ProximosPassos.ToArray()) };
        if (Revisao > 0) AuditoriaAnterior = new(Revisao, IdResponsavel, Origem, AtualizadoEm);
        Dados = novo; Sensibilidade = sensibilidade; IdResponsavel = idResponsavel; Origem = origem.Trim();
        AtualizadoEm = instante; ExpiraEm = expiraEm; Revisao++;
    }
}
