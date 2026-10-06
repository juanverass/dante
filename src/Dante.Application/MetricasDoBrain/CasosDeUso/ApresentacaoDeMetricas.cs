using System.Globalization;
using static Dante.Application.MetricasDoBrain.MetricasDoBrainAppService;

namespace Dante.Application.MetricasDoBrain;

internal static class ApresentacaoDeMetricas
{
    internal static string Formatar(ResumoDeMetricasDto r)
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
