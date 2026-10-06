using Dante.Application.CapturaDeConhecimento;
using Dante.Application.Conhecimentos;
using Dante.Application.ContextosDeTrabalho;
using Dante.Application.DocumentosFonte;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Projetos;
using Dante.Application.RelacoesDeConhecimento;

namespace Dante.Application.Mapeamento;

// Só compõe: cada feature declara seus pares/direções no próprio <Entidade>Mapping (#204).
// Feature nova com mapping entra nesta lista explícita, sem descoberta por reflexão.
internal static class MapeamentosDaApplication
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao)
    {
        EspacoDeConhecimentoMapping.Registrar(configuracao);
        ProjetoMapping.Registrar(configuracao);
        ConhecimentoMapping.Registrar(configuracao);
        ContextoDeTrabalhoMapping.Registrar(configuracao);
        CandidatoDeConhecimentoMapping.Registrar(configuracao);
        RelacaoDeConhecimentoMapping.Registrar(configuracao);
        DocumentoFonteMapping.Registrar(configuracao);
    }
}
