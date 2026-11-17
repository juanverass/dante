using Dante.Application.Mapeamento;
using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

internal static class VersaoDoAgenteMapping
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao) =>
        configuracao.Registrar<VersaoDoAgente, VersaoDoAgenteDto>(versao => new VersaoDoAgenteDto
        {
            Id = versao.Id,
            IdAgente = versao.IdAgente,
            Numero = versao.Numero,
            Configuracao = versao.Configuracao,
            HashDaConfiguracao = versao.HashDaConfiguracao,
            IdDoAutor = versao.IdDoAutor,
            CriadaEm = versao.CriadaEm
        });
}
