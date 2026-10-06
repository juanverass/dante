using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;
namespace Dante.Application.QualidadeDoBrain;

public interface IConsultaDeQualidade
{
    Task<IReadOnlyList<Conhecimento>> ListarAsync(AcessoAoBrain acesso, int deslocamento, int limite, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RelacaoDeConhecimento>> ListarRelacoesAsync(AcessoAoBrain acesso, IReadOnlyList<Guid> ids, int limite, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Conhecimento>> FiltrarElegiveisParaContextoAsync(AcessoAoBrain acesso, IReadOnlyList<Guid> ids, DateTimeOffset instante, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Conhecimento>> ElegiveisParaContextoAsync(AcessoAoBrain acesso, DateTimeOffset instante, int limite, CancellationToken cancellationToken = default);
}
