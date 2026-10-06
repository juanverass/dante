using static Dante.Application.MetricasDoBrain.MetricasDoBrainAppService;

namespace Dante.Application.MetricasDoBrain;

internal static class AgregacaoDeMetricas
{
    internal static ResumoDeMetricasDto Resumir(IReadOnlyList<MetricaDoBrainDto> registradas)
    {
        var metricas=registradas.OrderBy(x=>x.Em).ToArray();
        var envios=metricas.Where(x=>x.Tipo==MetricaDoBrainDto.Envio).ToArray();var turnos=metricas.Where(x=>x.Tipo==MetricaDoBrainDto.Turno).ToArray();
        var sessoes=metricas.Where(x=>x.Tipo!=MetricaDoBrainDto.Avaliacao).GroupBy(x=>x.IdSessao).OrderBy(x=>x.Min(m=>m.Em)).ToArray();
        int Historico(IEnumerable<MetricaDoBrainDto> sessao)=>sessao.Sum(x=>(x.TokensDoPedido??0)+(x.TokensDaResposta??0));
        var retomadas=new List<RetomadaMedidaDto>();var referencia=0;
        foreach(var sessao in sessoes)
        {
            var primeiro=sessao.FirstOrDefault(x=>x.Tipo==MetricaDoBrainDto.Envio);
            if(primeiro is {Injetados:>0} && referencia>0)
            {
                var brain=sessao.Where(x=>x.Tipo==MetricaDoBrainDto.Envio).Sum(x=>x.TokensDoPacote??0);
                retomadas.Add(new(sessao.Key,sessao.LastOrDefault(x=>x.Agente is not null)?.Agente,brain,referencia,(double)brain/referencia,
                    metricas.LastOrDefault(x=>x.Tipo==MetricaDoBrainDto.Avaliacao&&x.IdSessao==sessao.Key)));
            }
            referencia+=Historico(sessao);
        }
        double? razao=retomadas.Count==0?null:(double)retomadas.Sum(x=>x.TokensDoBrain)/retomadas.Sum(x=>x.TokensDeReferencia);
        var indicacao=razao switch
        {
            null=>"dados insuficientes: nenhuma retomada com histórico anterior medido neste escopo",
            <= LimiteDeGanho=>"ganho",<= LimiteDeNeutralidade=>"neutralidade",_=>"regressão"
        };
        var avaliacoes=retomadas.Select(x=>x.Avaliacao).OfType<MetricaDoBrainDto>().ToArray();var alertas=new List<string>();
        if(retomadas.Count>avaliacoes.Length)alertas.Add($"{retomadas.Count-avaliacoes.Length} retomada(s) sem avaliação");
        if(avaliacoes.Sum(x=>x.Repeticoes??0) is > 0 and var repeticoes)alertas.Add($"{repeticoes} informação(ões) essencial(is) repetida(s)");
        if(avaliacoes.Sum(x=>x.Esclarecimentos??0) is > 0 and var esclarecimentos)alertas.Add($"{esclarecimentos} esclarecimento(s) por contexto ausente");
        if(avaliacoes.Count(x=>x.Concluida==false) is > 0 and var falhas)alertas.Add($"{falhas} tarefa(s) não concluída(s)");
        if(avaliacoes.Count(x=>x.BuscouContextoAdicional==true) is > 0 and var buscas)alertas.Add($"{buscas} retomada(s) precisaram de contexto adicional");
        if(avaliacoes.Sum(x=>x.Incorretos??0) is > 0 and var incorretos)alertas.Add($"{incorretos} item(ns) incorreto(s) ou obsoleto(s) injetado(s)");
        var avaliados=avaliacoes.Sum(x=>(x.Relevantes??0)+(x.Irrelevantes??0)+(x.Incorretos??0));
        var usos=turnos.Where(x=>x.EntradaReportada is not null||x.SaidaReportada is not null).ToArray();
        var ultimo=envios.LastOrDefault(x=>x.ConhecimentosNoEscopo is not null);
        var comPacote=envios.Where(x=>x.Injetados>0).ToArray();var snapshots=envios.Where(x=>x.TokensDoSnapshot>0).ToArray();
        return new(sessoes.Length,envios.Length,envios.Count(x=>x.Bootstrap==true),turnos.Length,envios.Sum(x=>x.Recuperados??0),envios.Sum(x=>x.Selecionados??0),
            envios.Sum(x=>x.Injetados??0),envios.Sum(x=>x.Descartados??0),comPacote.Length==0?0:comPacote.Average(x=>x.TokensDoPacote??0),
            comPacote.Length==0?0:comPacote.Max(x=>x.TokensDoPacote??0),snapshots.Length==0?null:snapshots.Average(x=>x.TokensDoSnapshot!.Value),
            ultimo?.ConhecimentosNoEscopo,ultimo?.CaracteresNoEscopo,comPacote.Length==0?0:comPacote.Average(x=>x.CaracteresDoPacote??0),
            usos.Length==0?null:usos.Sum(x=>x.EntradaReportada??0),usos.Length==0?null:usos.Sum(x=>x.SaidaReportada??0),turnos.Length-usos.Length,
            retomadas,razao,indicacao,alertas,avaliados==0?null:(double)avaliacoes.Sum(x=>x.Relevantes??0)/avaliados);
    }
}
