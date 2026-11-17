using Dante.Application.Mapeamento;
using Dante.Domain.Agentes;

namespace Dante.Application.Agentes;

// Só leitura: agente e versões nascem e mudam pelos métodos do domínio, nunca de DTO.
internal static class AgenteMapping
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao) =>
        configuracao.Registrar<Agente, AgenteDto>(agente => new AgenteDto
        {
            Id = agente.Id,
            Handle = agente.Handle,
            Nome = agente.Nome,
            Descricao = agente.Descricao,
            Estado = agente.Estado,
            NumeroDaVersaoAtual = agente.NumeroDaVersaoAtual,
            CriadoEm = agente.CriadoEm,
            AtualizadoEm = agente.AtualizadoEm
        });
}
