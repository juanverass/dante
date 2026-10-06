namespace Dante.Application.Planilhas;

public interface ICadastroDePlanilhas
{
    Task<IReadOnlyList<PlanilhaCadastradaDto>> ListarAsync(CancellationToken cancellationToken = default);

    Task<PlanilhaCadastradaDto?> ObterAsync(string alias, CancellationToken cancellationToken = default);

    // Insere ou substitui pelo alias.
    Task SalvarAsync(PlanilhaCadastradaDto planilha, CancellationToken cancellationToken = default);

    Task<bool> RemoverAsync(string alias, CancellationToken cancellationToken = default);
}
