using Dante.Application.Comum;
using Dante.Domain.EspacosDeConhecimento;

namespace Dante.Application.EspacosDeConhecimento;

// Implementação concreta na persistência do Brain (#160).
public interface IEspacoDeConhecimentoRepository : IRepository<EspacoDeConhecimento>
{
    // Espaços do proprietário, filtrados por trecho do nome (sem distinção de maiúsculas) e, por padrão, só ativos.
    Task<IReadOnlyList<EspacoDeConhecimento>> ListarDoUsuarioAsync(Guid idUsuario, string? trechoDoNome,
        bool incluirArquivados, int limite, CancellationToken cancellationToken = default);
}
