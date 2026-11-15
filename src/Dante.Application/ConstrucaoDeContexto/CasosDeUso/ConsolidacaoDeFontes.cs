using System.Text;
using Dante.Application.BuscaDoBrain;
using Dante.Application.DocumentosFonte;
using Dante.Application.SegurancaDoBrain;
using static Dante.Application.ConstrucaoDeContexto.SelecaoDeContexto;

namespace Dante.Application.ConstrucaoDeContexto;

// Prova de consolidação de uma fonte bruta inteira (#147). Sem prova, a fonte segue integral.
// Prova = os trechos indexados da revisão citada cobrem o documento de 0 ao fim, sem lacuna, e cada trecho tem um
// conhecimento candidato ao pacote cuja proveniência registrada é exatamente a referência do trecho (id, revisão,
// parte e hash) e cujo conteúdo contém o texto do trecho. Co-ocorrência no snapshot ou semelhança de assunto não é prova.
// Cobertura: o índice grava Inicio em caracteres do PostgreSQL (code points = Rune). O documento é remontado pela
// união dos intervalos [Inicio, Inicio + runas); cada trecho precisa começar em ou antes do fim já coberto, a
// sobreposição precisa coincidir, e o documento remontado precisa ser a própria fonte. Maior fim alcançado não basta.
internal static class ConsolidacaoDeFontes
{
    internal const int MaximoDeTrechos = 32;
    internal static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> ProvarAsync(AcessoAoBrain acesso,
        IReadOnlyList<ItemDeContextoDto> candidatos, IIndiceDeFontes fontes, CancellationToken ct)
    {
        var provas = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var derivados = candidatos.Where(x => x.Origem == "conhecimento" && x.ReferenciaDaFonte is not null)
            .OrderBy(x => x.Chave, StringComparer.Ordinal).ToArray();
        if (derivados.Length == 0) return provas;
        foreach (var fonte in candidatos.Where(x => x.Origem == "fonte_bruta" && x.InicioDaFonte is null))
        {
            var cobertura = new List<string>(); var documento = new List<Rune>(); var completa = false;
            for (var numero = 0; numero < MaximoDeTrechos; numero++)
            {
                var trecho = await fontes.ObterTrechoAsync(acesso, fonte.Id, fonte.Revisao, numero, ct);
                if (trecho is null) { completa = numero > 0 && Remontado(documento) == fonte.Conteudo; break; }
                if (!Continuo(documento, trecho)) break;
                var texto = Normalizar(ProtecaoDeSegredos.Redigir(trecho.Conteudo)!);
                var derivado = derivados.FirstOrDefault(k => k.ReferenciaDaFonte == trecho.Referencia &&
                    Normalizar(k.Conteudo).Contains(texto, StringComparison.Ordinal));
                if (derivado is null || texto.Length == 0) break;
                cobertura.Add(derivado.Chave);
            }
            if (completa) provas[fonte.Chave] = cobertura;
        }
        return provas;
    }

    // Acrescenta o trecho ao documento remontado se ele começa dentro do já coberto (o primeiro, em 0) e a parte
    // sobreposta é idêntica. Lacuna, início deslocado ou sobreposição divergente invalidam a prova.
    private static bool Continuo(List<Rune> documento, TrechoDaFonteDto trecho)
    {
        if (trecho.Inicio < 0 || trecho.Inicio > documento.Count) return false;
        var runas = trecho.Conteudo.EnumerateRunes().ToArray();
        var sobreposta = Math.Min(documento.Count - trecho.Inicio, runas.Length);
        for (var i = 0; i < sobreposta; i++) if (documento[trecho.Inicio + i] != runas[i]) return false;
        documento.AddRange(runas.Skip(sobreposta));
        return true;
    }

    // A fonte candidata chega redigida; o documento remontado do índice passa pela mesma redação antes da comparação.
    private static string Remontado(List<Rune> documento)
    {
        var texto = new StringBuilder();
        foreach (var r in documento) texto.Append(r.ToString());
        return ProtecaoDeSegredos.Redigir(texto.ToString())!;
    }
}
