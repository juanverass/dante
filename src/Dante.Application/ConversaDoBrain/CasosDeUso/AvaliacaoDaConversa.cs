using Dante.Application.MetricasDoBrain;
using static Dante.Application.ConversaDoBrain.ApresentacaoDaConversa;

namespace Dante.Application.ConversaDoBrain;

public sealed class AvaliacaoDaConversa(MetricasDoBrainAppService metricas)
{
    // Avaliação humana da retomada (#148): campos omitidos ficam "não avaliados", nunca zero presumido.
    internal async Task<string> AvaliarAsync(ContextoDaConversa conversa, string corpo, CancellationToken cancellationToken)
    {
        const string formato="Diga: avalie a retomada: repetições: 0; esclarecimentos: 0; concluída: sim; contexto adicional: não; incorretos: 0; irrelevantes: 0; relevantes: 3. Campos omitidos ficam como não avaliados.";
        if(string.IsNullOrWhiteSpace(corpo))return formato;
        var avaliacao=new MetricaDoBrainDto();
        foreach(var parte in corpo.Split([';','\n',','],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
        {
            var colon=parte.IndexOf(':');var campo=colon<=0?"":ResolvedorDeIntencaoDoBrain.Normalizar(parte[..colon].Trim());
            var valor=colon<=0?"":ResolvedorDeIntencaoDoBrain.Normalizar(parte[(colon+1)..].Trim().TrimEnd('.'));
            int? numero=int.TryParse(valor,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var n)?n:null;
            bool? sim=valor is "sim" or "s"?true:valor is "nao" or "n"?false:null;
            MetricaDoBrainDto? lida=campo switch
            {
                "repeticoes" or "repeticao" or "repeti" when numero is not null=>avaliacao with{Repeticoes=numero},
                "esclarecimentos" or "esclarecimento" when numero is not null=>avaliacao with{Esclarecimentos=numero},
                "concluida" or "tarefa concluida" when sim is not null=>avaliacao with{Concluida=sim},
                "contexto adicional" or "buscou contexto" or "busquei contexto" when sim is not null=>avaliacao with{BuscouContextoAdicional=sim},
                "incorretos" or "obsoletos" or "incorretos ou obsoletos" when numero is not null=>avaliacao with{Incorretos=numero},
                "irrelevantes" when numero is not null=>avaliacao with{Irrelevantes=numero},
                "relevantes" when numero is not null=>avaliacao with{Relevantes=numero},
                _=>null
            };
            if(lida is null)return "Campo ou valor de avaliação inválido; nada foi registrado. "+formato;
            avaliacao=lida;
        }
        try
        {
            var sessao=await metricas.AvaliarAsync(conversa.Acesso,conversa.Pedido.IdSessao,avaliacao,cancellationToken);
            return $"Avaliação registrada para a sessão {sessao[(sessao.LastIndexOf(':')+1)..]}. Diga métricas do Brain para ver o resumo.";
        }
        catch(InvalidOperationException){return "Nenhuma conversa com contexto do Brain medida neste escopo; converse com o agente antes de avaliar.";}
    }

    internal async Task<string> MetricasAsync(ContextoDaConversa conversa, CancellationToken cancellationToken)
    {
        return MetricasDoBrainAppService.Formatar(await metricas.ResumirAsync(conversa.Acesso,cancellationToken));
    }
}
