namespace Dante.Application.Execucoes;

// Recuperação na inicialização (AD-67). Desconhecidas: unidades dante-agent-* que o ledger não registra como de um
// boot anterior; só relatadas, nunca paradas. ListagemIndisponivel: o gerenciador de unidades não respondeu (nada foi
// parado).
public sealed record RelatorioDeRecuperacaoDto(
    IReadOnlyList<string> UnidadesParadas,
    IReadOnlyList<string> UnidadesDesconhecidas,
    IReadOnlyList<string> FalhasAoParar,
    bool ListagemIndisponivel,
    int SessoesInterrompidas,
    int TrabalhosDevolvidos);
