using Dante.Application.Conhecimentos;
using Dante.Application.Mapeamento;
using Dante.Domain.RelacoesDeConhecimento;

namespace Dante.Application.RelacoesDeConhecimento;

internal static class RelacaoDeConhecimentoMapping
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao) =>
        configuracao.Registrar<RelacaoDeConhecimento, RelacaoDeConhecimentoDto>(x =>
            new RelacaoDeConhecimentoDto(x.Id, x.IdOrigem, x.IdDestino, x.Tipo, ConhecimentoMapping.ParaProvenienciaDto(x.Proveniencia), x.CriadaEm,
                x.IdConhecimentoEscolhido, x.ResolvidaEm, x.ProvenienciaDaResolucao == null ? null : ConhecimentoMapping.ParaProvenienciaDto(x.ProvenienciaDaResolucao)));
}
