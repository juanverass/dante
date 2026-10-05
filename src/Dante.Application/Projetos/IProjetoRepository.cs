using Dante.Application.Comum;
using Dante.Domain.Projetos;

namespace Dante.Application.Projetos;

// Implementação concreta na persistência do Brain (#160).
public interface IProjetoRepository : IRepository<Projeto>
{
    // Projetos do espaço, filtrados por trecho do nome (sem distinção de maiúsculas) e, por padrão, só ativos.
    Task<IReadOnlyList<Projeto>> ListarDoEspacoAsync(Guid idEspacoDeConhecimento, string? trechoDoNome,
        bool incluirArquivados, int limite, CancellationToken cancellationToken = default);
}
