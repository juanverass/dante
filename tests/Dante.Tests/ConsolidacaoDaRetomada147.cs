using System.Text.Json;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.DocumentosFonte;
using Dante.Domain.Conhecimentos;

namespace Dante.Tests;

// Consolidação condicional na retomada com fundamento (issue147-cenarios@2), conferida de forma independente do produto,
// só a partir do estado exportado e do texto do pacote. Por caso:
// - o Incidente e a Solucao precisam ter proveniência por trecho íntegra e igual ao pré-registro: referência canônica da
//   própria fonte e revisão, hash do documento, intervalo [0, s) e [s, fim) em pontos de código (s = início da linha
//   inicioDaSolucao), sha256 e TrechoDaFonte iguais ao texto do intervalo e conteúdo contendo o trecho;
// - a cobertura está comprovada quando os dois trechos são íntegros e os dois conhecimentos estão selecionados no pacote;
// - fonte omitida sem cobertura comprovada é divergência (perda de informação); fonte integral com cobertura comprovada
//   também é, porque o @2 existe para exercitar a omissão; conteúdo da fonte que não seja nem a íntegra nem o marcador de
//   omissão é divergência (fonte parcial apresentada como completa ou como omitida).
internal static class ConsolidacaoDaRetomada147
{
    internal enum SituacaoDaFonte { Ausente, Integral, Omitida }
    internal sealed record Caso(int Ordem, string Fonte, bool TrechosIntegros, bool Comprovada, SituacaoDaFonte Situacao);
    internal sealed record Resultado(IReadOnlyList<Caso> Casos, IReadOnlyList<string> Divergencias);

    internal static Resultado Verificar(string textoDoPacote, EstadoExportado147 estado, RoteiroDaRetomada147 roteiro, IReadOnlyList<CasoDaRetomada147> casos, string marcador)
    {
        var divergencias = new List<string>(); var resultado = new List<Caso>();
        var linhas = LinhasPorRef(textoDoPacote, divergencias);
        foreach (var caso in casos)
        {
            var fontes = estado.FontesDoCaso(roteiro, caso, marcador);
            if (fontes.Length != 1) { divergencias.Add($"caso {caso.Ordem}: fonte literal ausente ou duplicada"); continue; }
            var fonte = fontes[0]; var refFonte = EstadoExportado147.Ref("fonte", fonte);
            var documento = EstadoExportado147.Str(fonte, "Conteudo");
            var (citacaoIncidente, _) = roteiro.Citacoes(caso, marcador);
            var inicioDaSolucao = (citacaoIncidente + "\n").EnumerateRunes().Count(); var fim = documento.EnumerateRunes().Count();
            var identificador = RoteiroDaRetomada147.Identificador(caso.Ordem, marcador);
            var integros = true; var refs = new List<string>();
            foreach (var (tipo, a, b) in new[] { (TipoDeConhecimento.Incidente, 0, inicioDaSolucao), (TipoDeConhecimento.Solucao, inicioDaSolucao, fim) })
            {
                var k = estado.ConhecimentosDoCaso(tipo, identificador);
                if (k.Length != 1) { divergencias.Add($"caso {caso.Ordem}: {tipo} ausente ou duplicado"); integros = false; continue; }
                refs.Add(EstadoExportado147.Ref("conhecimento", k[0]));
                if (MotivoDeTrechoInvalido(k[0], fonte, documento, a, b) is { } motivo) { divergencias.Add($"caso {caso.Ordem}: trecho do {tipo} inválido: {motivo}"); integros = false; }
            }
            var selecionados = refs.Count == 2 && refs.All(linhas.ContainsKey);
            var comprovada = integros && selecionados;
            var situacao = SituacaoDaFonte.Ausente;
            if (linhas.TryGetValue(refFonte, out var linha))
            {
                var conteudo = linha.TryGetProperty("Conteudo", out var c) ? c.GetString() : null;
                situacao = conteudo == documento ? SituacaoDaFonte.Integral : SituacaoDaFonte.Omitida;
                if (situacao == SituacaoDaFonte.Omitida && conteudo != FormatoDeContexto.Omitido)
                    divergencias.Add($"caso {caso.Ordem}: conteúdo da fonte não é a íntegra nem o marcador de omissão");
                if (situacao == SituacaoDaFonte.Omitida && !comprovada)
                    divergencias.Add($"caso {caso.Ordem}: fonte omitida sem cobertura comprovada (trechos íntegros={integros}, selecionados={selecionados})");
                if (situacao == SituacaoDaFonte.Integral && comprovada)
                    divergencias.Add($"caso {caso.Ordem}: cobertura comprovada, mas a fonte seguiu integral");
                // Quando presente, ConsolidadaEm aponta exatamente os dois conhecimentos que cobrem a revisão.
                if (situacao == SituacaoDaFonte.Omitida && linha.TryGetProperty("ConsolidadaEm", out var em) &&
                    !em.EnumerateArray().Select(x => x.GetString()).Order().SequenceEqual(refs.Order()))
                    divergencias.Add($"caso {caso.Ordem}: ConsolidadaEm difere dos conhecimentos que cobrem a fonte");
            }
            resultado.Add(new(caso.Ordem, refFonte, integros, comprovada, situacao));
        }
        return new(resultado, divergencias);
    }

    // Referência canônica de trecho da própria fonte e revisão, com o intervalo esperado e o texto exato daquele intervalo.
    internal static string? MotivoDeTrechoInvalido(JsonElement conhecimento, JsonElement fonte, string documento, int inicio, int fim)
    {
        var revisao = EstadoExportado147.Int(conhecimento, "Revisao");
        var atual = conhecimento.GetProperty("Historico").EnumerateArray().Where(h => EstadoExportado147.Int(h, "Numero") == revisao).ToArray();
        if (atual.Length != 1) return "revisão atual sem histórico";
        var proveniencia = atual[0].GetProperty("Proveniencia");
        var referencia = proveniencia.TryGetProperty("ReferenciaDaFonte", out var r) ? r.GetString() : null;
        if (ReferenciaDeTrecho.Ler(referencia) is not { Inicio: { } a, Fim: { } b } t) return "referência não é trecho canônico: " + referencia;
        if (t.IdDocumento.ToString("D") != EstadoExportado147.Str(fonte, "Id") || t.Revisao != EstadoExportado147.Int(fonte, "Revisao")) return "referência aponta outra fonte ou revisão";
        if (t.HashDoDocumento != EstadoExportado147.Str(fonte, "Hash")) return "hash do documento diverge";
        if ((a, b) != (inicio, fim)) return $"intervalo {a}–{b} difere do pré-registro {inicio}–{fim}";
        var texto = string.Concat(documento.EnumerateRunes().Skip(a).Take(b - a));
        if (t.Sha256 != ReferenciaDeTrecho.HashDoTexto(texto)) return "sha256 do trecho diverge do texto do intervalo";
        if ((proveniencia.TryGetProperty("TrechoDaFonte", out var tr) ? tr.GetString() : null) != texto) return "TrechoDaFonte diverge do texto do intervalo";
        if (!SelecaoDeContexto.Normalizar(EstadoExportado147.Str(conhecimento, "Conteudo")).Contains(SelecaoDeContexto.Normalizar(texto), StringComparison.Ordinal))
            return "conteúdo não contém o trecho";
        return null;
    }

    // Linhas JSON do pacote por Ref; Ref repetida é divergência (não se escolhe uma das duas).
    private static Dictionary<string, JsonElement> LinhasPorRef(string texto, List<string> divergencias)
    {
        var linhas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var l in texto.Split('\n').Where(l => l.StartsWith('{')))
        {
            using var d = JsonDocument.Parse(l);
            if (!d.RootElement.TryGetProperty("Ref", out var r) || r.GetString() is not { } chave) continue;
            if (!linhas.TryAdd(chave, d.RootElement.Clone())) divergencias.Add("Ref repetida no pacote: " + chave);
        }
        return linhas;
    }
}
