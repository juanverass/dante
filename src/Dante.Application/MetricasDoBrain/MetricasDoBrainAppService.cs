using System.Globalization;
using Dante.Application.SegurancaDoBrain;
namespace Dante.Application.MetricasDoBrain;

// Compara o contexto enviado pelo Brain numa retomada com o histórico bruto das sessões anteriores do mesmo escopo
// (#148). Limiares explícitos e fixos: a economia é hipótese a validar, não critério ajustável pelo resultado.
public sealed class MetricasDoBrainAppService(IRegistroDeMetricasDoBrain registro, LeituraDoBrainAppService leitura, AutorizacaoDoBrain autorizacao)
{
    public const double LimiteDeGanho=0.5, LimiteDeNeutralidade=1.0;
    public async Task<string> AvaliarAsync(AcessoAoBrain acesso,string? idSessao,MetricaDoBrainDto avaliacao,CancellationToken cancellationToken=default)
    {
        await leitura.ValidarAcessoAsync(acesso,cancellationToken);var identidade=autorizacao.Identidade!;
        if(new[]{avaliacao.Repeticoes,avaliacao.Esclarecimentos,avaliacao.Incorretos,avaliacao.Irrelevantes,avaliacao.Relevantes}.Any(x=>x is < 0 or > 1000))
            throw new ArgumentException("Avaliação inválida.");
        var metricas=await registro.ListarAsync(identidade.IdTenant,acesso.IdUsuario,acesso.IdEspacoDeConhecimento,acesso.IdProjeto,cancellationToken);
        var sessao=idSessao is not null&&metricas.Any(x=>x.IdSessao==idSessao)?idSessao:
            metricas.Where(x=>x.Tipo==MetricaDoBrainDto.Envio).OrderBy(x=>x.Em).LastOrDefault()?.IdSessao;
        if(sessao is null)throw new InvalidOperationException("Nenhuma sessão com Brain neste escopo.");
        var agente=metricas.LastOrDefault(x=>x.IdSessao==sessao&&x.Agente is not null)?.Agente;
        await registro.RegistrarAsync(avaliacao with{Tipo=MetricaDoBrainDto.Avaliacao,Em=DateTimeOffset.UtcNow,IdTenant=identidade.IdTenant,IdUsuario=acesso.IdUsuario,
            IdEspacoDeConhecimento=acesso.IdEspacoDeConhecimento,IdProjeto=acesso.IdProjeto,IdSessao=sessao,Agente=agente},cancellationToken);
        return sessao;
    }
    public async Task<ResumoDeMetricasDto> ResumirAsync(AcessoAoBrain acesso,CancellationToken cancellationToken=default)
    {
        await leitura.ValidarAcessoAsync(acesso,cancellationToken);var identidade=autorizacao.Identidade!;
        return Resumir(await registro.ListarAsync(identidade.IdTenant,acesso.IdUsuario,acesso.IdEspacoDeConhecimento,acesso.IdProjeto,cancellationToken));
    }
    public static ResumoDeMetricasDto Resumir(IReadOnlyList<MetricaDoBrainDto> registradas)
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
    public static string Formatar(ResumoDeMetricasDto r)
    {
        var pt=CultureInfo.GetCultureInfo("pt-BR");string N(double x)=>x.ToString("N0",pt);string R(double x)=>x.ToString("N2",pt);
        string? Lido(long? x)=>x?.ToString("N0",pt);
        string Avaliacao(MetricaDoBrainDto? a)=>a is null?"sem avaliação":
            $"repetições {a.Repeticoes?.ToString(pt)??"?"}, esclarecimentos {a.Esclarecimentos?.ToString(pt)??"?"}, concluída {Sim(a.Concluida)}, "+
            $"contexto adicional {Sim(a.BuscouContextoAdicional)}, incorretos {a.Incorretos?.ToString(pt)??"?"}, relevantes {a.Relevantes?.ToString(pt)??"?"}"+
            $"/{(a.Relevantes is null&&a.Irrelevantes is null&&a.Incorretos is null?"?":((a.Relevantes??0)+(a.Irrelevantes??0)+(a.Incorretos??0)).ToString(pt))}";
        static string Sim(bool? x)=>x switch{true=>"sim",false=>"não",_=>"?"};
        var linhas=new List<string>
        {
            "Métricas do Brain neste escopo (locais; tokens estimados por ceil(bytes UTF-8/3), não pelo tokenizer dos agentes):",
            $"Sessões medidas: {r.Sessoes}; envios: {r.Envios} ({r.EnviosIniciais} iniciais); turnos: {r.Turnos}.",
            $"Recuperação: {N(r.Recuperados)} candidatos, {N(r.Selecionados)} selecionados, {N(r.Injetados)} injetados, {N(r.Descartados)} descartados.",
            $"Pacote: média {N(r.TokensMediosDoPacote)} tokens, máximo {N(r.TokensMaximosDoPacote)}; contexto de trabalho: "+
                (r.TokensMediosDoSnapshot is { } s?$"média {N(s)} tokens.":"não enviado."),
            r.ConhecimentosNoEscopo is { } k?$"Armazenado: {N(k)} conhecimento(s), {Lido(r.CaracteresNoEscopo)} caracteres; injetado por envio: {N(r.CaracteresMediosDoPacote)} caracteres"+
                (r.CaracteresMediosDoPacote>0?$" ({R((double)r.CaracteresNoEscopo!.Value/r.CaracteresMediosDoPacote)}:1).":"."):"Armazenado: indisponível.",
            $"Tokens informados pelos agentes: entrada {Lido(r.EntradaReportada)??"indisponível"}, saída {Lido(r.SaidaReportada)??"indisponível"}"+
                (r.TurnosSemUsoReportado>0?$" ({r.TurnosSemUsoReportado} turno(s) sem dado).":"."),
            $"Retomadas: {r.Retomadas.Count}."
        };
        linhas.AddRange(r.Retomadas.Select(x=>$"- {x.IdSessao[(x.IdSessao.LastIndexOf(':')+1)..]} ({x.Agente??"agente ?"}): Brain {N(x.TokensDoBrain)} tokens × histórico de referência "+
            $"{N(x.TokensDeReferencia)} = {R(x.Razao)}; {Avaliacao(x.Avaliacao)}."));
        linhas.Add($"Indicação: {r.Indicacao}"+(r.Razao is { } razao?$" (Brain/histórico = {R(razao)}; ganho ≤ {R(LimiteDeGanho)}, neutralidade ≤ {R(LimiteDeNeutralidade)}).":"."));
        linhas.Add("Qualidade: "+(r.AlertasDeQualidade.Count==0?"nenhum problema relatado":string.Join("; ",r.AlertasDeQualidade))+
            (r.Precisao is { } p?$"; precisão avaliada {R(p)}.":"."));
        linhas.Add("Avalie a retomada: avalie a retomada: repetições: 0; esclarecimentos: 0; concluída: sim; contexto adicional: não; incorretos: 0; irrelevantes: 0; relevantes: 3.");
        return string.Join('\n',linhas);
    }
}
