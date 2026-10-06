using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dante.Domain.Conhecimentos;
using Dante.Domain.RelacoesDeConhecimento;

namespace Dante.Application.QualidadeDoBrain;

internal static class AnaliseDeQualidade
{
    internal static RelatorioDeQualidadeDto Analisar(Conhecimento[] itens,
        IReadOnlyList<RelacaoDeConhecimento> arestas, DateTimeOffset agora,
        DateTimeOffset? confirmarAntesDe, bool paginaTruncada)
    {
        var achados = new List<AchadoDeQualidadeDto>();
        foreach (var item in itens)
        {
            if (item.Status is StatusDoConhecimento.Inativo or StatusDoConhecimento.Substituido)
                achados.Add(new(ProblemaDeQualidade.InativoOuSubstituido,item.Id,item.IdConhecimentoSubstituto,"Estado histórico; fora do contexto operacional."));
            else if (!item.EstaValidoEm(agora)) achados.Add(new(ProblemaDeQualidade.ForaDaValidade,item.Id,null,"Fora do intervalo de validade atual."));
            if (!item.Historico.Any(x => x.Proveniencia.ReferenciaDaFonte is not null))
                achados.Add(new(ProblemaDeQualidade.SemFonteReferenciada,item.Id,null,"Sem referência lógica à fonte; requer revisão manual."));
            if (arestas.Count <= 1000 && !arestas.Any(x => x.IdOrigem == item.Id || x.IdDestino == item.Id))
                achados.Add(new(ProblemaDeQualidade.Orfao,item.Id,null,"Sem relações no grafo; indicação de revisão, sem invalidação automática."));
            var confirmacoes = item.Historico.Where((r,i) => r.Status == StatusDoConhecimento.Confirmado && (i == 0 || item.Historico[i-1].Status != StatusDoConhecimento.Confirmado));
            if (confirmarAntesDe is { } corte && confirmacoes.LastOrDefault() is { } ultima && ultima.RegistradaEm < corte)
                achados.Add(new(ProblemaDeQualidade.ConfirmacaoAnteriorAoLimite,item.Id,null,"Última confirmação anterior ao limite solicitado."));
        }
        for (var i=0;i<itens.Length;i++) for (var j=i+1;j<itens.Length;j++)
        {
            var a=itens[i]; var b=itens[j];
            if (!a.EstaValidoEm(agora) || !b.EstaValidoEm(agora) || a.Tipo != b.Tipo) continue;
            if (PossivelContradicao(a,b)) achados.Add(new(ProblemaDeQualidade.PossivelContradicao,a.Id,b.Id,"Valores diferentes para a mesma chave estruturada ou negação textual próxima; exige evidência/decisão."));
            else if (Normalizar(a.Conteudo) == Normalizar(b.Conteudo) && a.Conteudo is not null && a.DadosEstruturados == b.DadosEstruturados || Semelhanca(a.Conteudo,b.Conteudo) >= 0.85)
                achados.Add(new(ProblemaDeQualidade.PossivelDuplicata,a.Id,b.Id,"Conteúdo idêntico/próximo no mesmo tipo/escopo; consolidação somente explícita."));
        }
        var conflitos = arestas.Take(1000).Where(x => x.Tipo == TipoDeRelacao.Contradiz).Select(x =>
            new ConflitoDeConhecimentoDto(x.Id,x.IdOrigem,x.IdDestino,x.IdConhecimentoEscolhido,x.ResolvidaEm)).ToArray();
        foreach(var conflito in conflitos.Where(x => x.ResolvidoEm is null))
            achados.Add(new(ProblemaDeQualidade.ContradicaoExplicita,conflito.IdOrigem,conflito.IdDestino,"Conflito explícito não resolvido; nenhum lado entra automaticamente no contexto."));
        return new(achados.Take(500).ToArray(),conflitos,paginaTruncada || arestas.Count > 1000 || achados.Count > 500,itens.Length);
    }
    private static string Normalizar(string? texto)=>Regex.Replace((texto??"").Normalize(NormalizationForm.FormC).Trim(),@"\s+"," ",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    private static double Semelhanca(string? a,string? b)
    {
        if(string.IsNullOrWhiteSpace(a)||string.IsNullOrWhiteSpace(b))return 0;
        var x=Palavras(a);var y=Palavras(b); if(x.Count<4||y.Count<4)return 0;
        return (double)x.Intersect(y).Count()/x.Union(y).Count();
    }
    private static HashSet<string> Palavras(string x)=>Regex.Matches(Normalizar(x).ToLowerInvariant(),@"[\p{L}\p{N}_]+",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))
        .Select(m=>m.Value).ToHashSet();
    private static bool PossivelContradicao(Conhecimento a,Conhecimento b)
    {
        if(a.DadosEstruturados is not null && b.DadosEstruturados is not null)
        {
            using var x=JsonDocument.Parse(a.DadosEstruturados);using var y=JsonDocument.Parse(b.DadosEstruturados);
            if(x.RootElement.TryGetProperty("chave",out var chave) && y.RootElement.TryGetProperty("chave",out var outra) && chave.GetRawText()==outra.GetRawText() &&
                x.RootElement.TryGetProperty("valor",out var valor) && y.RootElement.TryGetProperty("valor",out var outroValor) && valor.GetRawText()!=outroValor.GetRawText())return true;
        }
        var px=Palavras(a.Conteudo??"");var py=Palavras(b.Conteudo??"");
        return px.Contains("não")!=py.Contains("não") && Semelhanca(a.Conteudo,b.Conteudo)>=0.65;
    }
}
