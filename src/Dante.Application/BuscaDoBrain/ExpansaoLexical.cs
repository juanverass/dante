using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dante.Application.BuscaDoBrain;

// Segunda tentativa conservadora: alternativas de grupos completos, sem descartar âncoras desconhecidas.
// Vocabulário deliberadamente pequeno; não tenta resolver polissemia nem interpretar operadores do usuário.
internal static class ExpansaoLexical
{
    private static readonly string[] Limpeza = ["limpeza", "limpar", "faxina", "higienização", "higienizar"];
    private static readonly string[] Encerramento = ["encerramento", "encerrar", "finalização", "finalizar", "término", "terminar"];
    private static readonly Regex LinguagemSimples = new(@"\A[\p{L}\p{N}\s_.?!,]+\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex OperadorOuNegacao = new(@"\b(?:or|não|nao|nunca|sem|exceto)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Palavras = new(@"[\p{L}\p{N}][\p{L}\p{N}_.]*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> Vazias = new(StringComparer.Ordinal)
    {
        "a", "o", "as", "os", "um", "uma", "umas", "uns", "de", "do", "da", "dos", "das", "em", "no", "na", "nos", "nas", "ao", "aos", "e",
        "para", "como", "com", "sobre", "que", "qual", "quais", "quando", "onde", "por", "pelo", "pela"
    };

    internal static string? Tentar(string texto)
    {
        if (!LinguagemSimples.IsMatch(texto) || OperadorOuNegacao.IsMatch(texto)) return null;
        var termos = Palavras.Matches(texto).Select(m => m.Value.ToLowerInvariant()).ToArray();
        if (termos.Any(t => t.Length > 64)) return null;
        termos = termos.Where(t => !Vazias.Contains(Chave(t))).Distinct(StringComparer.Ordinal).ToArray();
        if (termos.Length is < 2 or > 8) return null;

        var alternativas = new List<string> { "" }; var mudou = false;
        foreach (var termo in termos)
        {
            var chave = Chave(termo);
            string[] grupo = Limpeza.Any(t => Chave(t) == chave) ? Limpeza : Encerramento.Any(t => Chave(t) == chave) ? Encerramento :
                chave is "subprocesso" or "subprocessos" ? [termo, "processos filhos", "processos descendentes"] : [termo];
            mudou |= grupo.Length > 1;
            if (alternativas.Count * grupo.Length > 24) return null;
            alternativas = alternativas.SelectMany(a => grupo.Select(g => a.Length == 0 ? g : a + " " + g)).ToList();
        }
        if (!mudou) return null;
        var consulta = string.Join(" or ", alternativas);
        return consulta.Length <= 2000 ? consulta : null;
    }

    private static string Chave(string texto) => string.Concat(texto.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark));

}
