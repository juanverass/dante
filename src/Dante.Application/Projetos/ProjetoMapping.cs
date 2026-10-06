using Dante.Application.Mapeamento;
using Dante.Domain.Projetos;

namespace Dante.Application.Projetos;

// #136: a criação passa pelo constructor do domínio, sem aceitar Id, repositório nem estado do DTO.
internal static class ProjetoMapping
{
    internal static void Registrar(ConfiguracaoMapeamento configuracao)
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
