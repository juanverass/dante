using Dante.Application.Mapeamento;
using Dante.Domain.EspacosDeConhecimento;

namespace Dante.Application.EspacosDeConhecimento;

// #152: a criação passa pelo constructor do domínio, sem aceitar Id nem estado do DTO.
internal static class EspacoDeConhecimentoMapping
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao)
    {
        configuracao.Registrar<EspacoDeConhecimento, EspacoDeConhecimentoDto>(espaco => new EspacoDeConhecimentoDto
        {
            Id = espaco.Id,
            IdUsuario = espaco.IdUsuario,
            IdTenant = espaco.IdTenant,
            Nome = espaco.Nome,
            Descricao = espaco.Descricao,
            Arquivado = espaco.Arquivado
        });
        configuracao.Registrar<EspacoDeConhecimentoDto, EspacoDeConhecimento>(dto =>
            new EspacoDeConhecimento(dto.IdUsuario, dto.Nome, dto.Descricao, dto.IdTenant));
    }
}
