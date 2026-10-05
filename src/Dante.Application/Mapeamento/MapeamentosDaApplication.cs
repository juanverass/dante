using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Projetos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;

namespace Dante.Application.Mapeamento;

internal static class MapeamentosDaApplication
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao)
    {
        // Os contratos funcionais serão adicionados aqui nas respectivas issues.
        // Cada direção exige expressão explícita, incluindo campos sensíveis/IDs expostos.
        RegistrarEspacosDeConhecimento(configuracao);
        RegistrarProjetos(configuracao);
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

    // #136: a criação passa pelo constructor do domínio, sem aceitar Id, repositório nem estado do DTO.
    private static void RegistrarProjetos(ConfiguracaoMapeamento configuracao)
    {
        configuracao.Registrar<Projeto, ProjetoDto>(projeto => new ProjetoDto
        {
            Id = projeto.Id,
            IdEspacoDeConhecimento = projeto.IdEspacoDeConhecimento,
            Nome = projeto.Nome,
            Descricao = projeto.Descricao,
            AliasDoRepositorio = projeto.AliasDoRepositorio,
            Arquivado = projeto.Arquivado
        });
        configuracao.Registrar<ProjetoDto, Projeto>(dto =>
            new Projeto(dto.IdEspacoDeConhecimento, dto.Nome, dto.Descricao));
    }
}
