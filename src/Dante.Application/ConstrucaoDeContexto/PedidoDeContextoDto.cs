using Dante.Application.BuscaDoBrain;
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
