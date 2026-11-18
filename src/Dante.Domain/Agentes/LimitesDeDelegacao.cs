namespace Dante.Domain.Agentes;

// Limites de delegação de uma versão de agente (AD-70, #309). Teto é o limite global aprovado para esta etapa: uma
// versão só restringe, nunca passa dele. Uma versão sem limites próprios usa o teto (LimitesDoAgente segue o mesmo
// princípio de "nulo é sem limite próprio"). Profundidade 1 = só filhas diretas da raiz; paralelismo 1 = uma filha por
// vez, enquanto o limite global de execução for 1 (AD-63 sem autorização operacional). Elevar o teto exige nova
// decisão, alteração deste tipo e das constraints do banco.
public sealed record LimitesDeDelegacao(
    int ProfundidadeMaxima,
    int DelegacoesPorExecucao,
    int MinutosParaIniciar,
    int MinutosDeDuracao,
    int Paralelismo)
{
    public static LimitesDeDelegacao Teto { get; } = new(1, 4, 30, 60, 1);

    internal void Validar()
    {
        Faixa(ProfundidadeMaxima, Teto.ProfundidadeMaxima, nameof(ProfundidadeMaxima), "Profundidade de delegação");
        Faixa(DelegacoesPorExecucao, Teto.DelegacoesPorExecucao, nameof(DelegacoesPorExecucao), "Delegações por execução");
        Faixa(MinutosParaIniciar, Teto.MinutosParaIniciar, nameof(MinutosParaIniciar), "Prazo para iniciar a delegação");
        Faixa(MinutosDeDuracao, Teto.MinutosDeDuracao, nameof(MinutosDeDuracao), "Duração da delegação");
        Faixa(Paralelismo, Teto.Paralelismo, nameof(Paralelismo), "Paralelismo de delegações");
    }

    private static void Faixa(int valor, int teto, string parametro, string nome)
    {
        if (valor < 1 || valor > teto)
            throw new ArgumentOutOfRangeException(parametro, $"{nome} deve ficar entre 1 e {teto}.");
    }
}
