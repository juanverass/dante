using Dante.Application.Mapeamento;
using Dante.Domain.DocumentosFonte;

namespace Dante.Application.DocumentosFonte;

internal static class DocumentoFonteMapping
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao) =>
        configuracao.Registrar<DocumentoFonte, DocumentoFonteDto>(x =>
            new DocumentoFonteDto(x.Id, x.IdEspacoDeConhecimento, x.IdProjeto, x.Origem,
                x.Formato, x.Hash, x.Revisao, x.Sensibilidade, x.AtualizadoEm, x.Removido));
}
