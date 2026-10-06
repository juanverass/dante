using Dante.Application.BuscaDoBrain;
using Dante.Domain.Conhecimentos;
namespace Dante.Application.ConstrucaoDeContexto;

public sealed record PedidoDeContextoDto
{
    public string Mensagem { get; init; } = "";
    public BuscaDoBrainSearchDto Filtros { get; init; } = new();
    public int OrcamentoDeTokens { get; init; } = 2048;
    public int LimiteDeItens { get; init; } = 12;
    public int LimiteDeCandidatos { get; init; } = 50;
    public int ProfundidadeDeRelacoes { get; init; } = 1;
    public bool IncluirSnapshot { get; init; } = true;
    public IReadOnlyList<string> FragmentosJaPresentes { get; init; } = [];
    // Chave → revisão já injetadas na conversa upstream atual (refresh); revisão nova volta a ser elegível.
    public IReadOnlyDictionary<string, int> JaInjetados { get; init; } = new Dictionary<string, int>();
}
public sealed record ItemDeContextoDto(string Chave, Guid Id, int Revisao, string Origem, TipoDeConhecimento? Tipo,
    StatusDoConhecimento? Status, Sensibilidade Sensibilidade, string Conteudo, string? Referencia,
    string Motivo, double Relevancia, int TokensEstimados, int? InicioDaFonte = null);
public sealed record RegistroDeContextoDto(string Chave, Guid Id, string Origem, string Estado, string Motivo, int TokensEstimados);
public sealed record CustoDeContextoDto(int TokensEstimados,int Itens,int Caracteres,int BytesUtf8,int Orcamento);
public sealed record PacoteDeContextoDto(Guid Id, string TextoParaInjecao, IReadOnlyList<ItemDeContextoDto> Itens,
    IReadOnlyList<RegistroDeContextoDto> Registros, CustoDeContextoDto Custo, bool RecuperacaoLimitada)
{
    // O adapter só chama após envio bem-sucedido. Construção não equivale a injeção.
    public PacoteDeContextoDto RegistrarInjecao(IReadOnlyCollection<string> chaves)
    {
        var selecionadas=Itens.Select(x=>x.Chave).ToHashSet(StringComparer.Ordinal);
        if(chaves.Distinct(StringComparer.Ordinal).Count()!=chaves.Count || chaves.Any(x=>!selecionadas.Contains(x)))
            throw new ArgumentException("Injeção contém item não selecionado ou repetido.");
        return this with{Registros=Registros.Select(x=>chaves.Contains(x.Chave)?x with{Estado="injetado"}:x).ToArray()};
    }
}
