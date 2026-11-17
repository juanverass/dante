namespace Dante.Application.Execucoes;

// Worktrees de execução de escrita (AD-71, #307), configuráveis em Orquestracao:Worktrees. Os limites contam execuções
// que estão escrevendo (reserva ativa), não processos: global por host e por repositório (checkout principal).
// O código suporta limites maiores que 1 (e os testes os exercitam), mas o host recusa na composição qualquer valor
// acima de 1 enquanto a contenção estrita da AD-63 não tiver ativação operacional autorizada: elevar o limite é uma
// alteração própria, não uma chave de configuração.
public sealed record OpcoesDeWorktrees
{
    public const int LimiteOperacional = 1;

    public string Raiz { get; init; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dante", "worktrees");

    public int LimiteGlobal { get; init; } = 1;
    public int LimitePorRepositorio { get; init; } = 1;

    // Espera antes de reavaliar um trabalho que encontrou a capacidade ocupada; não conta tentativa.
    public TimeSpan EsperaPorCapacidade { get; init; } = TimeSpan.FromSeconds(30);

    public void Validar(bool paralelismoAutorizado = false)
    {
        if (string.IsNullOrWhiteSpace(Raiz) || !System.IO.Path.IsPathFullyQualified(Raiz))
            throw new ArgumentException("A raiz das worktrees precisa ser um caminho absoluto.");
        if (LimiteGlobal < 1 || LimitePorRepositorio < 1)
            throw new ArgumentException("Os limites de worktrees precisam ser positivos.");
        if (LimitePorRepositorio > LimiteGlobal)
            throw new ArgumentException("O limite por repositório não pode passar do limite global.");
        if (!paralelismoAutorizado && LimiteGlobal > LimiteOperacional)
            throw new ArgumentException(
                "Limite global de worktrees acima de 1 exige a ativação autorizada da contenção estrita (AD-63).");
        if (EsperaPorCapacidade <= TimeSpan.Zero) throw new ArgumentException("Espera por capacidade inválida.");
    }
}
