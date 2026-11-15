using Dante.Application.BuscaDoBrain;
using Dante.Application.CapturaDeConhecimento;
using Dante.Application.Conhecimentos;
using Dante.Application.ContextosDeTrabalho;
using Dante.Application.DocumentosFonte;
using Dante.Application.QualidadeDoBrain;
using Dante.Application.RelacoesDeConhecimento;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;
using static Dante.Application.ConstrucaoDeContexto.SelecaoDeContexto;
namespace Dante.Application.ConstrucaoDeContexto;

public sealed class ConstrutorDeContextoAppService(BuscaDoBrainAppService busca, LeituraDoBrainAppService leitura,
    IRelacaoDeConhecimentoRepository relacoes, IConsultaDeQualidade qualidade,
    PoliticaDeSensibilidade politica, ContextoDeTrabalhoAppService snapshots, IIndiceDeFontes fontes,
    ICandidatoDeConhecimentoRepository pendentes, IDocumentoFonteRepository documentos)
{
    public static int EstimarTokens(string texto) => SelecaoDeContexto.EstimarTokens(texto);
    public async Task<PacoteDeContextoDto> ConstruirAsync(AcessoAoBrain acesso,PedidoDeContextoDto pedido,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(pedido);await leitura.ValidarAcessoAsync(acesso,cancellationToken);
        ConstrucaoDeContextoValidator.ValidarPedido(pedido);
        var automatico=acesso with{PermitirSecreto=false};
        var termo=string.IsNullOrWhiteSpace(pedido.Filtros.Texto)?pedido.Mensagem:pedido.Filtros.Texto;
        // Termos derivados da mensagem: na busca híbrida, casar só parte deles exige apoio semântico (#280).
        var integral=string.IsNullOrWhiteSpace(pedido.Filtros.Texto)?null:pedido.Mensagem;
        var resultado=await busca.BuscarAsync(automatico,pedido.Filtros with{Texto=termo,TextoIntegral=integral,IdConhecimento=null,Limite=pedido.LimiteDeCandidatos,Deslocamento=0},cancellationToken);
        var motivos=new Dictionary<Guid,(double Score,string Motivo)>();var brutas=new List<ResultadoDaBuscaDto>();
        foreach(var r in resultado.Resultados)
            if(r.Origem==OrigemDoResultado.Conhecimento) motivos.TryAdd(r.Item.Id,(r.Score,r.Explicacao));else brutas.Add(r);
        var snapshot=pedido.IncluirSnapshot?await snapshots.RetomarAsync(automatico,cancellationToken):null;
        var referencias=snapshot?.Dados.Referencias.Select(ReferenciaTipadaDeContexto.Ler).OfType<ReferenciaTipadaDeContexto>().Distinct().ToArray()??[];
        var conhecimentosReferenciados=referencias.Where(r=>r.Tipo=="conhecimento").GroupBy(r=>r.Id).ToDictionary(g=>g.Key,g=>g.ToArray());
        var fontesReferenciadas=referencias.Where(r=>r.Tipo=="fonte").Select(r=>r.Id).ToHashSet();
        var registros=new List<RegistroDeContextoDto>();
        if(snapshot is not null)foreach(var id in snapshot.Dados.IdsDecisoesConfirmadas)motivos.TryAdd(id,(0.01,"Decisão referenciada pelo snapshot operacional."));
        foreach(var grupo in conhecimentosReferenciados)
        {
            if(grupo.Value.Length>1)
            {
                motivos.Remove(grupo.Key);
                foreach(var r in grupo.Value)registros.Add(ResolucaoDeReferenciasDeContexto.Descartar(r,ResolucaoDeReferenciasDeContexto.Conflitante));
            }
            else motivos[grupo.Key]=(0.02,"Referência tipada do snapshot: conhecimento.");
        }
        var limitou=resultado.TemMais;var fronteira=motivos.Keys.ToArray();var visitados=fronteira.ToHashSet();
        for(var nivel=1;nivel<=pedido.ProfundidadeDeRelacoes && fronteira.Length>0;nivel++)
        {
            var vizinhas=await relacoes.ListarVizinhasAsync(automatico.IdEspacoDeConhecimento,automatico.IdProjeto,fronteira,101,cancellationToken);
            limitou|=vizinhas.Count>100;var proximas=new List<Guid>();
            foreach(var r in vizinhas.Take(100))
            {
                if(r.Tipo is TipoDeRelacao.Substitui or TipoDeRelacao.Contradiz)continue;
                foreach(var id in new[]{r.IdOrigem,r.IdDestino})
                {
                    if(visitados.Contains(id))continue;
                    if(visitados.Count>=200){limitou=true;continue;}
                    visitados.Add(id);proximas.Add(id);
                    motivos.TryAdd(id,(0.005/nivel,$"Expansão por {r.Tipo}, relação {r.Id:D}, nível {nivel}."));
                }
            }
            fronteira=proximas.ToArray();
        }
        var elegiveis=await qualidade.FiltrarElegiveisParaContextoAsync(automatico,motivos.Keys.ToArray(),DateTimeOffset.UtcNow,cancellationToken);
        var camposDoSnapshot=new List<CampoDoSnapshot>();
        var candidatos=new List<ItemDeContextoDto>();
        var presentes=pedido.FragmentosJaPresentes.Append(pedido.Mensagem).Select(Normalizar).ToArray();
        foreach(var id in motivos.Keys.Except(elegiveis.Select(x=>x.Id)))
        {
            if(conhecimentosReferenciados.TryGetValue(id,out var refs))
            { if(refs.Length==1)registros.Add(ResolucaoDeReferenciasDeContexto.Descartar(refs[0],ResolucaoDeReferenciasDeContexto.Indisponivel)); }
            else registros.Add(new($"conhecimento:{id:D}",id,"conhecimento","descartado","Obsoleto, conflitante, fora dos filtros/escopo ou sem autorização.",0));
        }
        foreach(var k in elegiveis)
        {
            conhecimentosReferenciados.TryGetValue(k.Id,out var refs);
            if(refs is { Length: > 1 })continue;
            var referencia=refs?.Single();
            if(referencia is not null && k.Revisao!=referencia.Revisao)
            {registros.Add(ResolucaoDeReferenciasDeContexto.Descartar(referencia,ResolucaoDeReferenciasDeContexto.Divergente));continue;}
            if(!ElegibilidadeDeContexto.AtendeFiltros(k, pedido.Filtros))
            {registros.Add(new($"conhecimento:{k.Id:D}",k.Id,"conhecimento","descartado","Fora dos filtros da solicitação.",0));continue;}
            if(snapshot?.Dados.IdsDecisoesConfirmadas.Contains(k.Id)==true &&
                (k.Tipo!=TipoDeConhecimento.Decisao || k.Status!=StatusDoConhecimento.Confirmado))
            {registros.Add(new($"conhecimento:{k.Id:D}",k.Id,"conhecimento","descartado","Decisão do snapshot perdeu confirmação.",0));continue;}
            var dto=politica.Projetar(k,automatico,FinalidadeDeLeitura.ContextoAutomatico);
            if(dto.ConteudoProtegido || referencia is not null && (ProtecaoDeSegredos.ContemSegredo(k.Conteudo)||ProtecaoDeSegredos.ContemSegredo(k.DadosEstruturados)))
            {if(referencia is not null)registros.Add(ResolucaoDeReferenciasDeContexto.Descartar(referencia,ResolucaoDeReferenciasDeContexto.Indisponivel));continue;}
            var conteudo=ProtecaoDeSegredos.Redigir(string.Join("\n",new[]{dto.Conteudo,dto.DadosEstruturados}.Where(x=>!string.IsNullOrWhiteSpace(x))))!;
            // Título em chave própria só se a redação por partes reproduz exatamente o conteúdo redigido inteiro.
            var titulo=FormatoDeContexto.TituloExclusivo(dto.DadosEstruturados) is { } t && !string.IsNullOrWhiteSpace(dto.Conteudo) &&
                ProtecaoDeSegredos.Redigir(dto.Conteudo)+"\n"+ProtecaoDeSegredos.Redigir(dto.DadosEstruturados)==conteudo?ProtecaoDeSegredos.Redigir(t):null;
            var m=motivos[k.Id];candidatos.Add(new($"conhecimento:{k.Id:D}",k.Id,k.Revisao,"conhecimento",k.Tipo,k.Status,k.Sensibilidade,conteudo,referencia?.Chave??dto.ReferenciaDaFonte,m.Motivo,m.Score,0,NaturezaDeOrigem:k.NaturezaDeOrigem,Natureza:k.Natureza,Referenciado:referencia is not null,ReferenciaDaFonte:dto.ReferenciaDaFonte,
                Titulo:titulo,CorpoSemTitulo:titulo is null?null:ProtecaoDeSegredos.Redigir(dto.Conteudo)));
        }
        foreach(var r in brutas)
        {
            // A referência fixa a revisão do documento inteiro; não usar trechos atuais como fallback.
            if(fontesReferenciadas.Contains(r.Item.Id))continue;
            var f=r.Fonte!;var atual=await fontes.ObterTrechoAsync(automatico,f.IdDocumento,f.Revisao,f.Numero,cancellationToken);
            if(atual is null){registros.Add(new(f.Referencia,f.IdDocumento,"fonte_bruta","descartado","Fonte mudou ou foi removida.",0));continue;}
            candidatos.Add(new(f.Referencia,f.IdDocumento,f.Revisao,"fonte_bruta",null,null,r.Item.Sensibilidade,ProtecaoDeSegredos.Redigir(atual.Conteudo)!,f.Referencia,"Trecho relevante de fonte bruta; exige consolidação para ser fato.",r.Score,0,f.Inicio,AutoriaDaFonte:atual.Autoria));
        }
        await ResolucaoDeReferenciasDeContexto.ResolverAsync(referencias,automatico,pedido.Filtros,pendentes,documentos,candidatos,registros,cancellationToken);
        if(snapshot is not null)
        {
            var d=snapshot.Dados;
            // Campos nomeados (#147): o papel de cada frase (objetivo, pendência, próximo passo) chega ao agente.
            var campos=new (string Campo,string? Texto)[]{("Objetivo",d.Objetivo),("Tarefa",d.Tarefa),("Progresso",d.Progresso),("UltimoResultado",d.UltimoResultado)}
                .Concat(d.Referencias.Where(r=>ReferenciaTipadaDeContexto.Ler(r) is null).Select(r=>("Referencias",(string?)r)))
                .Concat(d.Pendencias.Select(x=>("Pendencias",(string?)x))).Concat(d.ProximosPassos.Select(x=>("ProximosPassos",(string?)x)))
                .Where(x=>!string.IsNullOrWhiteSpace(x.Item2)).Select(x=>new CampoDoSnapshot(x.Item1,ProtecaoDeSegredos.Redigir(x.Item2)!))
                .Where(x=>!Coberto(x.Texto,presentes)).Distinct();
            camposDoSnapshot=campos.ToList();
            var texto=string.Join("\n",camposDoSnapshot.Select(x=>x.Texto));
            if(texto.Length>0)candidatos.Add(new($"snapshot:{snapshot.Id:D}",snapshot.Id,snapshot.Revisao,"snapshot",null,null,snapshot.Sensibilidade,texto,null,"Snapshot operacional ativo, separado de fatos e do transcript.",0,0));
        }
        var idsDeConhecimento=candidatos.Where(x=>x.Origem=="conhecimento").Select(x=>x.Id).Distinct().ToArray();
        var entre=new List<RelacaoEntreItens>();
        // Relações entre candidatos (#147), paginadas nos limites do repositório; acima deles o pacote segue sem elas.
        if(idsDeConhecimento.Length is > 1 and <= 201)
        {
            var conhecidos=idsDeConhecimento.ToHashSet();
            for(var pagina=0;pagina<5;pagina++)
            {
                var janela=await relacoes.ListarVizinhasAsync(automatico.IdEspacoDeConhecimento,automatico.IdProjeto,idsDeConhecimento,101,cancellationToken,pagina*101);
                foreach(var r in janela)if(conhecidos.Contains(r.IdOrigem) && conhecidos.Contains(r.IdDestino))entre.Add(new(r.IdOrigem,r.IdDestino,r.Tipo));
                if(janela.Count<101)break;
            }
        }
        var consolidacoes=await ConsolidacaoDeFontes.ProvarAsync(automatico,candidatos,fontes,cancellationToken);
        return SelecaoDeContexto.Selecionar(pedido, candidatos, camposDoSnapshot, registros, limitou, entre, consolidacoes);
    }
}
