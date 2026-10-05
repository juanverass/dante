using Dante.Application.EspacosDeConhecimento;
using Dante.Domain.EspacosDeConhecimento;

namespace Dante.Application.Mapeamento;

internal static class MapeamentosDaApplication
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao)
    {
        // Os contratos funcionais serão adicionados aqui nas respectivas issues.
        // Cada direção exige expressão explícita, incluindo campos sensíveis/IDs expostos.
        RegistrarEspacosDeConhecimento(configuracao);
    }

    // #152: a criação passa pelo constructor do domínio, sem aceitar Id nem estado do DTO.
    private static void RegistrarEspacosDeConhecimento(ConfiguracaoMapeamento configuracao)
    {
        configuracao.Registrar<EspacoDeConhecimento, EspacoDeConhecimentoDto>(espaco => new EspacoDeConhecimentoDto
        {
            Id = espaco.Id,
            IdUsuario = espaco.IdUsuario,
            Nome = espaco.Nome,
            Descricao = espaco.Descricao,
            Arquivado = espaco.Arquivado
        });
        configuracao.Registrar<EspacoDeConhecimentoDto, EspacoDeConhecimento>(dto =>
            new EspacoDeConhecimento(dto.IdUsuario, dto.Nome, dto.Descricao));
    }
}
