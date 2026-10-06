using System.Text;
using System.Text.Json;
using Dante.Application.Conhecimentos;
using Dante.Application.DocumentosFonte;
using Dante.Application.EspacosDeConhecimento;
using Dante.Application.Projetos;
using Dante.Application.RelacoesDeConhecimento;
using Dante.Application.Mapeamento;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;
namespace Dante.Application.AuditoriaDoBrain;

public sealed class InspecaoDoBrainAppService(IConsultaDeAuditoria consulta, IMapsterTypeAdapter mapper,
    AutorizacaoDoBrain autorizacao, IEspacoDeConhecimentoRepository espacos)
{
    public async Task<IReadOnlyList<EspacoDeConhecimentoDto>> ListarEspacosAsync(CancellationToken cancellationToken=default)
    {
        var identidade=autorizacao.Identidade ?? throw new UnauthorizedAccessException("Identidade não resolvida.");
        return (await espacos.ListarDoUsuarioAsync(identidade.IdUsuario,null,true,100,cancellationToken))
            .Select(x=>Redigir(mapper.Mapear<Dante.Domain.EspacosDeConhecimento.EspacoDeConhecimento,EspacoDeConhecimentoDto>(x))).ToArray();
    }
    public async Task<AuditoriaDoBrainDto> InspecionarAsync(AcessoAoBrain acesso, AuditoriaDoBrainSearchDto? filtro=null, CancellationToken cancellationToken=default) =>
        Projetar(await consulta.ConsultarAsync(acesso,filtro??new(),false,cancellationToken),acesso,false);
    public async Task<ExportacaoDoBrainDto> ExportarAsync(AcessoAoBrain acesso,CancellationToken cancellationToken=default)
    {
        var dados=await consulta.ConsultarAsync(acesso,new(){Limite=1000},true,cancellationToken);
        if(dados.TemMais) throw new InvalidOperationException("Exportação excede o limite; selecione um projeto menor.");
        var dto=Projetar(dados,acesso,true);
        var json=JsonSerializer.Serialize(dto,new JsonSerializerOptions{WriteIndented=true});
        if(Encoding.UTF8.GetByteCount(json)>10_000_000) throw new InvalidOperationException("Exportação excede 10 MB; selecione um projeto menor.");
        var md=new StringBuilder("# Exportação do Brain\n\n");md.AppendLine("Formato: 1. Fontes brutas não são fatos confirmados.\n");
        Bloco(md,dto.Espaco.Nome);md.AppendLine($"Espaço: {dto.Espaco.Id:D}\n");
        foreach(var p in dto.Projetos){md.AppendLine($"## Projeto {p.Id:D}");Bloco(md,p.Nome);Bloco(md,p.Descricao);}
        foreach(var k in dto.Conhecimentos)
        {
            md.AppendLine($"## {k.Tipo} — {k.Id:D}");md.AppendLine($"Status: {k.Status}; sensibilidade: {k.Sensibilidade}; revisão: {k.Revisao}; confiança: {k.Confianca?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"não informada"}.");
            md.AppendLine($"Projeto: {k.IdProjeto?.ToString("D")??"sem projeto"}; validade: {k.ValidoDesde:O} / {k.ValidoAte:O}.");
            Bloco(md,k.Conteudo);Bloco(md,k.DadosEstruturados);Bloco(md,string.Join(", ",k.Tags));
            Bloco(md,$"Origem: {k.Proveniencia.Origem}\nReferência: {k.Proveniencia.ReferenciaDaFonte}\nRevisão: {k.Proveniencia.RevisaoDaFonte}\nTrecho: {k.Proveniencia.TrechoDaFonte}");
        }
        foreach(var r in dto.Relacoes) md.AppendLine($"Relação {r.Id:D}: {r.IdOrigem:D} → {r.Tipo} → {r.IdDestino:D}.");
        foreach(var f in dto.Fontes){md.AppendLine($"## Fonte bruta — {f.Documento.Id:D}");Bloco(md,$"{f.Documento.Origem}\n{f.Documento.Hash}\nRevisão {f.Documento.Revisao}");Bloco(md,f.Conteudo);}
        return new(md.ToString(),json);
    }
    private AuditoriaDoBrainDto Projetar(DadosDeAuditoria dados,AcessoAoBrain acesso,bool exportacao)
    {
        bool Permitida(Sensibilidade classe)=>classe<Sensibilidade.Confidencial || classe==Sensibilidade.Confidencial && acesso.PermitirConfidencial || !exportacao && classe==Sensibilidade.Secreto && acesso.PermitirSecreto;
        ProvenienciaDto Prova(ProvenienciaDto p)=>p with{Origem=ProtecaoDeSegredos.Redigir(p.Origem)!,ReferenciaDaFonte=ProtecaoDeSegredos.Redigir(p.ReferenciaDaFonte),RevisaoDaFonte=ProtecaoDeSegredos.Redigir(p.RevisaoDaFonte),TrechoDaFonte=ProtecaoDeSegredos.Redigir(p.TrechoDaFonte)};
        var itens=dados.Conhecimentos.Select(x=>SaidaAutorizadaDoBrain.Projetar(mapper.Mapear<Conhecimento,ConhecimentoDto>(x),autorizacao))
            .Select(x=>x with{Historico=x.Historico.Where(r=>Permitida(r.Sensibilidade)).ToArray()}).ToArray();
        var porId=dados.Conhecimentos.ToDictionary(x=>x.Id);
        bool ProvaPermitida(RelacaoDeConhecimento r,DateTimeOffset instante)=>new[]{r.IdOrigem,r.IdDestino}.All(id=>
            porId.TryGetValue(id,out var k) && Permitida(k.Historico.LastOrDefault(h=>h.RegistradaEm<=instante)?.Sensibilidade??k.Sensibilidade));
        var relacoes=dados.Relacoes.Select(x=>
        {
            var dto=mapper.Mapear<RelacaoDeConhecimento,RelacaoDeConhecimentoDto>(x);
            return dto with{Proveniencia=ProvaPermitida(x,x.CriadaEm)?Prova(dto.Proveniencia):new(){IdResponsavel=dto.Proveniencia.IdResponsavel,Origem="proveniência protegida"},
                ProvenienciaDaResolucao=dto.ProvenienciaDaResolucao is null?null:ProvaPermitida(x,x.ResolvidaEm!.Value)?Prova(dto.ProvenienciaDaResolucao):new(){Origem="proveniência protegida"}};
        }).ToArray();
        return new(1,Redigir(mapper.Mapear<Dante.Domain.EspacosDeConhecimento.EspacoDeConhecimento,EspacoDeConhecimentoDto>(dados.Espaco)),
            dados.Projetos.Select(x=>mapper.Mapear<Dante.Domain.Projetos.Projeto,ProjetoDto>(x)).Select(x=>x with{Nome=ProtecaoDeSegredos.Redigir(x.Nome)!,Descricao=ProtecaoDeSegredos.Redigir(x.Descricao),AliasDoRepositorio=ProtecaoDeSegredos.Redigir(x.AliasDoRepositorio)}).ToArray(),
            itens,relacoes,dados.Fontes.Select(x=>new FonteAuditadaDto(mapper.Mapear<Dante.Domain.DocumentosFonte.DocumentoFonte,DocumentoFonteDto>(x) with{Origem=ProtecaoDeSegredos.Redigir(x.Origem)!},ProtecaoDeSegredos.Redigir(x.Conteudo)!)).ToArray(),dados.TemMais);
    }
    private static EspacoDeConhecimentoDto Redigir(EspacoDeConhecimentoDto x)=>x with{Nome=ProtecaoDeSegredos.Redigir(x.Nome)!,Descricao=ProtecaoDeSegredos.Redigir(x.Descricao)};
    private static void Bloco(StringBuilder destino,string? texto)
    {
        if(string.IsNullOrEmpty(texto)) return;
        var max=0;var atual=0;foreach(var ch in texto){atual=ch=='`'?atual+1:0;max=Math.Max(max,atual);}
        var cerca=new string('`',Math.Max(3,max+1));destino.AppendLine(cerca+"text").AppendLine(texto).AppendLine(cerca).AppendLine();
    }
}
