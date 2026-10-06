namespace Dante.Application.Planilhas;

// Registro local e só de acréscimo das escritas em planilhas.
public interface IAuditoriaDePlanilhas
{
    Task RegistrarAsync(RegistroDeAuditoriaDePlanilha registro, CancellationToken cancellationToken = default);
}
