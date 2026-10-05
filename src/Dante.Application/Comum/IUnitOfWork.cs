namespace Dante.Application.Comum;

// Confirmação provider-agnostic das alterações registradas nos repositories; implementação concreta na #168.
public interface IUnitOfWork
{
    Task SalvarAlteracoesAsync(CancellationToken cancellationToken = default);
}
