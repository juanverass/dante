using Dante.Application.Conhecimentos;
using Dante.Application.Mapeamento;
using Dante.Domain.CapturaDeConhecimento;

namespace Dante.Application.CapturaDeConhecimento;

internal static class CandidatoDeConhecimentoMapping
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao) =>
        configuracao.Registrar<CandidatoDeConhecimento, CandidatoDeConhecimentoDto>(x => new CandidatoDeConhecimentoDto(
            x.Id, x.IdEspacoDeConhecimento, x.IdProjeto, x.Tipo, x.Conteudo, x.Sensibilidade, x.Natureza,
            x.Modo, x.Estado, x.Revisao, x.IdConhecimento, x.Historico.Select(a => new AtoDoCandidatoDto(a.Revisao, a.Acao,
                a.Tipo, a.Conteudo, a.Sensibilidade, a.Justificativa, ConhecimentoMapping.ParaProvenienciaDto(a.Proveniencia), a.Instante, a.Estado, a.IdConhecimento, a.Titulo, a.Tags)).ToArray(), x.Titulo, x.Tags));
}
