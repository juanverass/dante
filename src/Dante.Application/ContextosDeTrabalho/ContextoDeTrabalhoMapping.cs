using Dante.Application.Mapeamento;
using Dante.Domain.ContextosDeTrabalho;

namespace Dante.Application.ContextosDeTrabalho;

internal static class ContextoDeTrabalhoMapping
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao) =>
        configuracao.Registrar<ContextoDeTrabalho, ContextoDeTrabalhoDto>(x =>
            new ContextoDeTrabalhoDto(x.Id, x.IdEspacoDeConhecimento, x.IdProjeto, x.Dados,
                x.Sensibilidade, x.Revisao, x.IdResponsavel, x.Origem, x.AtualizadoEm, x.ExpiraEm, x.AuditoriaAnterior));
}
