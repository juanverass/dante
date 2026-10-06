using Dante.Application.BuscaDoBrain;
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
    PoliticaDeSensibilidade politica, ContextoDeTrabalhoAppService snapshots, IIndiceDeFontes fontes)
{
    public static int EstimarTokens(string texto) => SelecaoDeContexto.EstimarTokens(texto);
    public async Task<PacoteDeContextoDto> ConstruirAsync(AcessoAoBrain acesso,PedidoDeContextoDto pedido,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(pedido);await leitura.ValidarAcessoAsync(acesso,cancellationToken);
        ConstrucaoDeContextoValidator.ValidarPedido(pedido);
        var automatico=acesso with{PermitirSecreto=false};
        var termo=string.IsNullOrWhiteSpace(pedido.Filtros.Texto)?pedido.Mensagem:pedido.Filtros.Texto;
        var resultado=await busca.BuscarAsync(automatico,pedido.Filtros with{Texto=termo,IdConhecimento=null,Limite=pedido.LimiteDeCandidatos,Deslocamento=0},cancellationToken);
        var motivos=new Dictionary<Guid,(double Score,string Motivo)>();var brutas=new List<ResultadoDaBuscaDto>();
        foreach(var r in resultado.Resultados)
            if(r.Origem==OrigemDoResultado.Conhecimento) motivos.TryAdd(r.Item.Id,(r.Score,r.Explicacao));else brutas.Add(r);
        var snapshot=pedido.IncluirSnapshot?await snapshots.RetomarAsync(automatico,cancellationToken):null;
        if(snapshot is not null)foreach(var id in snapshot.Dados.IdsDecisoesConfirmadas)motivos.TryAdd(id,(0.01,"Decisão referenciada pelo snapshot operacional."));
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
        var camposDoSnapshot=new List<string>();
        var candidatos=new List<ItemDeContextoDto>();var registros=new List<RegistroDeContextoDto>();
        var presentes=pedido.FragmentosJaPresentes.Append(pedido.Mensagem).Select(Normalizar).ToArray();
        foreach(var id in motivos.Keys.Except(elegiveis.Select(x=>x.Id))) registros.Add(new($"conhecimento:{id:D}",id,"conhecimento","descartado","Obsoleto, conflitante, fora dos filtros/escopo ou sem autorização.",0));
        foreach(var k in elegiveis)
        {
            if(!ElegibilidadeDeContexto.AtendeFiltros(k, pedido.Filtros))
            {registros.Add(new($"conhecimento:{k.Id:D}",k.Id,"conhecimento","descartado","Fora dos filtros da solicitação.",0));continue;}
            if(motivos[k.Id].Motivo.StartsWith("Decisão referenciada",StringComparison.Ordinal) &&
                (k.Tipo!=TipoDeConhecimento.Decisao || k.Status!=StatusDoConhecimento.Confirmado))
            {registros.Add(new($"conhecimento:{k.Id:D}",k.Id,"conhecimento","descartado","Decisão do snapshot perdeu confirmação.",0));continue;}
            var dto=politica.Projetar(k,automatico,FinalidadeDeLeitura.ContextoAutomatico);
            if(dto.ConteudoProtegido)continue;
            var conteudo=string.Join("\n",new[]{dto.Conteudo,dto.DadosEstruturados}.Where(x=>!string.IsNullOrWhiteSpace(x)));
            var m=motivos[k.Id];candidatos.Add(new($"conhecimento:{k.Id:D}",k.Id,k.Revisao,"conhecimento",k.Tipo,k.Status,k.Sensibilidade,conteudo,dto.ReferenciaDaFonte,m.Motivo,m.Score,0));
        }
        foreach(var r in brutas)
        {
            var f=r.Fonte!;var atual=await fontes.ObterTrechoAsync(automatico,f.IdDocumento,f.Revisao,f.Numero,cancellationToken);
            if(atual is null){registros.Add(new(f.Referencia,f.IdDocumento,"fonte_bruta","descartado","Fonte mudou ou foi removida.",0));continue;}
            candidatos.Add(new(f.Referencia,f.IdDocumento,f.Revisao,"fonte_bruta",null,null,r.Item.Sensibilidade,ProtecaoDeSegredos.Redigir(atual.Conteudo)!,f.Referencia,"Trecho relevante de fonte bruta; exige consolidação para ser fato.",r.Score,0,f.Inicio));
        }
        if(snapshot is not null)
        {
            var d=snapshot.Dados;
            var campos=new[]{d.Objetivo,d.Tarefa,d.Progresso,d.UltimoResultado}.Concat(d.Referencias).Concat(d.Pendencias).Concat(d.ProximosPassos)
                .Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>ProtecaoDeSegredos.Redigir(x)!).Where(x=>!Coberto(x,presentes)).Distinct(StringComparer.Ordinal);
            camposDoSnapshot=campos.ToList();
            var texto=string.Join("\n",camposDoSnapshot);
            if(texto.Length>0)candidatos.Add(new($"snapshot:{snapshot.Id:D}",snapshot.Id,snapshot.Revisao,"snapshot",null,null,snapshot.Sensibilidade,texto,null,"Snapshot operacional ativo, separado de fatos e do transcript.",0,0));
        }
        return SelecaoDeContexto.Selecionar(pedido, candidatos, camposDoSnapshot, registros, limitou);
    }
}
