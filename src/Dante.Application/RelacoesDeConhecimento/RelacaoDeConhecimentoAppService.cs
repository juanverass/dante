using Dante.Application.Mapeamento;
using Dante.Application.Comum;
using Dante.Application.Conhecimentos;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Projetos;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;
namespace Dante.Application.RelacoesDeConhecimento;

public sealed class RelacaoDeConhecimentoAppService(IRelacaoDeConhecimentoRepository relacoes,
    IConhecimentoRepository conhecimentos, IEspacoDeConhecimentoRepository espacos, IProjetoRepository projetos,
    IUnitOfWork unitOfWork, IMapsterTypeAdapter typeAdapter) : IRelacaoDeConhecimentoAppService
{
    public async Task<RelacaoDeConhecimentoDto> RelacionarAsync(Guid idEspaco, Guid? idProjeto, Guid idOrigem, Guid idDestino,
        TipoDeRelacao tipo, ProvenienciaDto proveniencia, CancellationToken cancellationToken = default)
    {
        var espaco = await espacos.ObterPorIdAsync(idEspaco, cancellationToken) ?? throw new ArgumentException("Espaço não encontrado.");
        if (espaco.Arquivado) throw new InvalidOperationException("Espaço arquivado é somente leitura.");
        if (idProjeto is not null)
        {
            var projeto = await projetos.ObterPorIdAsync(idProjeto.Value, cancellationToken) ?? throw new ArgumentException("Projeto não encontrado.");
            if (projeto.IdEspacoDeConhecimento != idEspaco || projeto.Arquivado) throw new InvalidOperationException("Projeto inválido ou arquivado.");
        }
        var origem = await ObterNoEscopoAsync(idEspaco, idProjeto, idOrigem, cancellationToken);
        var destino = await ObterNoEscopoAsync(idEspaco, idProjeto, idDestino, cancellationToken);
        var relacao = new RelacaoDeConhecimento(origem, destino, tipo, ConhecimentoAppService.ParaProveniencia(proveniencia), DateTimeOffset.UtcNow);
        var existente = await relacoes.ObterEquivalenteAsync(relacao, cancellationToken);
        if (existente is not null) return ParaDto(existente);
        await relacoes.AdicionarAsync(relacao, cancellationToken);
        await unitOfWork.SalvarAlteracoesAsync(cancellationToken);
        return ParaDto(relacao);
    }

    public async Task<VizinhancaDto> ConsultarVizinhancaAsync(Guid idEspaco, Guid? idProjeto, Guid idRaiz,
        int profundidade = 1, int limite = 50, CancellationToken cancellationToken = default)
    {
        if (profundidade is < 1 or > 3 || limite is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(profundidade));
        await ObterNoEscopoAsync(idEspaco, idProjeto, idRaiz, cancellationToken);
        var visitados = new HashSet<Guid> { idRaiz }; var fronteira = new HashSet<Guid> { idRaiz };
        var resultado = new Dictionary<Guid, RelacaoDeConhecimentoDto>();
        for (var nivel = 0; nivel < profundidade && fronteira.Count > 0; nivel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Uma consulta por nível, <= 101 linhas por consulta; ciclos nunca reabrem a fronteira.
            var vizinhas = await relacoes.ListarVizinhasAsync(idEspaco, idProjeto, fronteira, limite + 1, cancellationToken);
            var seguinte = new HashSet<Guid>();
            foreach (var relacao in vizinhas)
            {
                if (resultado.ContainsKey(relacao.Id)) continue;
                if (resultado.Count == limite) return new(resultado.Values.ToArray(), true);
                resultado.Add(relacao.Id, ParaDto(relacao));
                foreach (var id in new[] { relacao.IdOrigem, relacao.IdDestino }) if (visitados.Add(id)) seguinte.Add(id);
            }
            if (vizinhas.Count == limite + 1) return new(resultado.Values.ToArray(), true);
            fronteira = seguinte;
        }
        return new(resultado.Values.ToArray(), false);
    }

    private async Task<Conhecimento> ObterNoEscopoAsync(Guid espaco, Guid? projeto, Guid id, CancellationToken ct)
    {
        if (espaco == Guid.Empty || projeto == Guid.Empty || id == Guid.Empty) throw new ArgumentException("Escopo inválido.");
        var item = await conhecimentos.ObterPorIdAsync(id, ct);
        if (item is null || item.IdEspacoDeConhecimento != espaco || item.IdProjeto != projeto)
            throw new ArgumentException("Conhecimento não encontrado no escopo.");
        return item;
    }
    private RelacaoDeConhecimentoDto ParaDto(RelacaoDeConhecimento x) => typeAdapter.Mapear<RelacaoDeConhecimento, RelacaoDeConhecimentoDto>(x);
}
