using Dante.Application.Mapeamento;
using Dante.Domain.Grupos;

namespace Dante.Application.Grupos;

// Só leitura: grupo e versões nascem e mudam pelos métodos do domínio, nunca de DTO. As versões entram no DTO pelo
// caso de uso, que resolve os handles dos participantes.
internal static class GrupoDeAgentesMapping
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao) =>
        configuracao.Registrar<GrupoDeAgentes, GrupoDeAgentesDto>(grupo => new GrupoDeAgentesDto
        {
            Id = grupo.Id,
            Nome = grupo.Nome,
            Descricao = grupo.Descricao,
            Estado = grupo.Estado,
            NumeroDaVersaoAtual = grupo.NumeroDaVersaoAtual,
            NumeroDaVersaoAtiva = grupo.NumeroDaVersaoAtiva,
            CriadoEm = grupo.CriadoEm,
            AtualizadoEm = grupo.AtualizadoEm
        });
}
