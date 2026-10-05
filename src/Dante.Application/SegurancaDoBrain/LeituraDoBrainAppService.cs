using Dante.Application.Conhecimentos;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Projetos;
namespace Dante.Application.SegurancaDoBrain;

public sealed class LeituraDoBrainAppService(IConhecimentoRepository conhecimentos,
    IEspacoDeConhecimentoRepository espacos, IProjetoRepository projetos, PoliticaDeSensibilidade politica)
{
    public async Task<LeituraProtegidaDto?> LerAsync(Guid id, AcessoAoBrain acesso, FinalidadeDeLeitura finalidade,
        CancellationToken cancellationToken = default)
    {
        await ValidarAcessoAsync(acesso, cancellationToken);
        var item = await conhecimentos.ObterPorIdAsync(id, cancellationToken);
        if (item is null || item.IdEspacoDeConhecimento != acesso.IdEspacoDeConhecimento || item.IdProjeto != acesso.IdProjeto) return null;
        return politica.Projetar(item, acesso, finalidade);
    }
    public async Task ValidarAcessoAsync(AcessoAoBrain acesso, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acesso);
        var espaco = await espacos.ObterPorIdAsync(acesso.IdEspacoDeConhecimento, cancellationToken);
        if (acesso.IdUsuario == Guid.Empty || espaco is null || espaco.IdUsuario != acesso.IdUsuario || espaco.Arquivado)
            throw new UnauthorizedAccessException("Espaço não autorizado ou arquivado.");
        if (acesso.IdProjeto is not null)
        {
            var projeto = await projetos.ObterPorIdAsync(acesso.IdProjeto.Value, cancellationToken);
            if (projeto is null || projeto.IdEspacoDeConhecimento != espaco.Id || projeto.Arquivado)
                throw new UnauthorizedAccessException("Projeto não autorizado ou arquivado.");
        }
    }
}
