namespace Dante.Application.Contextos;

// Porta de leitura do estado Git dos checkouts (AD-58). Nunca altera o repositório e nunca lança: falha vira Erro.
public interface ILeitorDeCheckouts
{
    Task<EstadoDoCheckout> LerAsync(string caminho, CancellationToken cancellationToken = default);
}
