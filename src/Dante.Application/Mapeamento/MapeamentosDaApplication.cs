using Dante.Application.CapturaDeConhecimento;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Application.RelacoesDeConhecimento;
using Dante.Domain.RelacoesDeConhecimento;
using Dante.Application.Conhecimentos;
using Dante.Domain.Conhecimentos;
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
        RegistrarConhecimentos(configuracao);
        configuracao.Registrar<Dante.Domain.ContextosDeTrabalho.ContextoDeTrabalho, Dante.Application.ContextosDeTrabalho.ContextoDeTrabalhoDto>(x =>
            new Dante.Application.ContextosDeTrabalho.ContextoDeTrabalhoDto(x.Id, x.IdEspacoDeConhecimento, x.IdProjeto, x.Dados,
                x.Sensibilidade, x.Revisao, x.IdResponsavel, x.Origem, x.AtualizadoEm, x.ExpiraEm, x.AuditoriaAnterior));
        configuracao.Registrar<CandidatoDeConhecimento, CandidatoDeConhecimentoDto>(x => new CandidatoDeConhecimentoDto(
            x.Id, x.IdEspacoDeConhecimento, x.IdProjeto, x.Tipo, x.Conteudo, x.Sensibilidade, x.Natureza,
            x.Modo, x.Estado, x.Revisao, x.IdConhecimento, x.Historico.Select(a => new AtoDoCandidatoDto(a.Revisao, a.Acao,
                a.Tipo, a.Conteudo, a.Sensibilidade, a.Justificativa, ParaProvenienciaDto(a.Proveniencia), a.Instante, a.Estado, a.IdConhecimento)).ToArray()));
        configuracao.Registrar<RelacaoDeConhecimento, RelacaoDeConhecimentoDto>(x =>
            new RelacaoDeConhecimentoDto(x.Id, x.IdOrigem, x.IdDestino, x.Tipo, ParaProvenienciaDto(x.Proveniencia), x.CriadaEm,
                x.IdConhecimentoEscolhido, x.ResolvidaEm, x.ProvenienciaDaResolucao == null ? null : ParaProvenienciaDto(x.ProvenienciaDaResolucao)));
    }

    // #152: a criação passa pelo constructor do domínio, sem aceitar Id nem estado do DTO.
    private static void RegistrarEspacosDeConhecimento(ConfiguracaoMapeamento configuracao)
    {
        configuracao.Registrar<Dante.Domain.DocumentosFonte.DocumentoFonte, Dante.Application.DocumentosFonte.DocumentoFonteDto>(x =>
            new Dante.Application.DocumentosFonte.DocumentoFonteDto(x.Id, x.IdEspacoDeConhecimento, x.IdProjeto, x.Origem,
                x.Formato, x.Hash, x.Revisao, x.Sensibilidade, x.AtualizadoEm, x.Removido));
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
    private static void RegistrarConhecimentos(ConfiguracaoMapeamento configuracao)
    {
        configuracao.Registrar<ConhecimentoDto, Conhecimento>(dto => new Conhecimento(dto.IdEspacoDeConhecimento,
            dto.IdProjeto, dto.Tipo, dto.Conteudo, dto.DadosEstruturados, dto.Status, dto.Confianca, dto.Sensibilidade,
            dto.ValidoDesde, dto.ValidoAte, dto.Tags, ConhecimentoAppService.ParaProveniencia(dto.Proveniencia), DateTimeOffset.UtcNow));
        configuracao.Registrar<Conhecimento, ConhecimentoDto>(entidade => ParaConhecimentoDto(entidade));
    }

    private static ProvenienciaDto ParaProvenienciaDto(ProvenienciaDoConhecimento origem) => new()
    {
        IdResponsavel = origem.IdResponsavel, Origem = origem.Origem,
        ReferenciaDaFonte = origem.ReferenciaDaFonte, RevisaoDaFonte = origem.RevisaoDaFonte, TrechoDaFonte = origem.TrechoDaFonte
    };

    private static ConhecimentoDto ParaConhecimentoDto(Conhecimento entidade) => new()
    {
        Id = entidade.Id, IdEspacoDeConhecimento = entidade.IdEspacoDeConhecimento, IdProjeto = entidade.IdProjeto,
        IdAutor = entidade.IdAutor, Tipo = entidade.Tipo, Conteudo = entidade.Conteudo, DadosEstruturados = entidade.DadosEstruturados,
        Status = entidade.Status, Confianca = entidade.Confianca, Sensibilidade = entidade.Sensibilidade,
        CriadoEm = entidade.CriadoEm, AtualizadoEm = entidade.AtualizadoEm, ValidoDesde = entidade.ValidoDesde,
        ValidoAte = entidade.ValidoAte, Tags = entidade.Tags.ToArray(), Proveniencia = ParaProvenienciaDto(entidade.Proveniencia),
        Revisao = entidade.Revisao, IdConhecimentoSubstituto = entidade.IdConhecimentoSubstituto,
        Historico = entidade.Historico.Select(revisao => new RevisaoDoConhecimentoDto
        {
            Numero = revisao.Numero, Tipo = revisao.Tipo, Conteudo = revisao.Conteudo, DadosEstruturados = revisao.DadosEstruturados,
            Status = revisao.Status, Confianca = revisao.Confianca, Sensibilidade = revisao.Sensibilidade,
            ValidoDesde = revisao.ValidoDesde, ValidoAte = revisao.ValidoAte, Tags = revisao.Tags.ToArray(),
            Proveniencia = ParaProvenienciaDto(revisao.Proveniencia), RegistradaEm = revisao.RegistradaEm,
            IdConhecimentoSubstituto = revisao.IdConhecimentoSubstituto
        }).ToArray()
    };
}
