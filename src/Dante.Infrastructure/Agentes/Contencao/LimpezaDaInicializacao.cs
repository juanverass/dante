namespace Dante.Infrastructure.Agentes;

// As etapas são independentes: perder o bootstrap não pode impedir stop nem confirmação do scope.
internal static class LimpezaDaInicializacao
{
    internal static async Task<AggregateException?> TentarAsync(
        Exception falhaOriginal,
        Func<CancellationToken, Task> encerrarBootstrap,
        Func<CancellationToken, Task> pararUnidade,
        Func<CancellationToken, Task> confirmarVazio,
        TimeSpan? prazoPorEtapa = null)
    {
        var erros = new List<Exception>();
        var prazo = prazoPorEtapa ?? TimeSpan.FromSeconds(5);
        foreach (var (nome, executar) in new (string, Func<CancellationToken, Task>)[]
        {
            ("encerrar o bootstrap", encerrarBootstrap),
            ("parar a unidade", pararUnidade),
            ("confirmar raiz encerrada e contenção vazia", confirmarVazio)
        })
        {
            // Nunca usa o token cancelado do lançamento nem o prazo já consumido pela etapa anterior.
            using var limite = new CancellationTokenSource(prazo);
            try { await executar(limite.Token).WaitAsync(limite.Token); }
            catch (OperationCanceledException erro) when (limite.IsCancellationRequested)
            {
                erros.Add(new TimeoutException("Prazo excedido ao " + nome + ".", erro));
            }
            catch (Exception erro) { erros.Add(new InvalidOperationException("Falha ao " + nome + ".", erro)); }
        }
        return erros.Count == 0 ? null : new AggregateException(new[] { falhaOriginal }.Concat(erros));
    }
}
