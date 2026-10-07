using System.Globalization;
using System.Text;

namespace Dante.Application.Planilhas;

// Política pura de busca (#224): compara o texto procurado com os valores exibidos já lidos, sem diferenciar
// maiúsculas, acentos e espaços repetidos. Correspondência exata da célula vem antes da parcial; o agente decide o
// significado, e a escrita por referência só prossegue com um único candidato.
internal static class BuscaNaPlanilha
{
    internal const int MaximoDeCelulasNaLinha = 15;

    internal sealed record Encontrada(OcorrenciaNaPlanilhaDto Ocorrencia, bool Exata);

    internal static (IReadOnlyList<Encontrada> Encontradas, bool Truncado) Buscar(IEnumerable<ValoresLidos> fontes,
        string termo, int limite)
    {
        var procurado = Normalizar(termo);
        var exatas = new List<Encontrada>();
        var parciais = new List<Encontrada>();
        foreach (var fonte in fontes)
        {
            for (var i = 0; i < fonte.Linhas.Count; i++)
            {
                var linha = fonte.Linhas[i];
                for (var j = 0; j < linha.Count; j++)
                {
                    var valor = Normalizar(linha[j]);
                    if (valor.Length == 0 || !valor.Contains(procurado, StringComparison.Ordinal)) continue;
                    var exata = valor == procurado;
                    (exata ? exatas : parciais).Add(new Encontrada(Ocorrencia(fonte, i, j), exata));
                }
            }
        }
        var todas = exatas.Concat(parciais).ToArray();
        return (todas.Take(limite).ToArray(), todas.Length > limite);
    }

    // Os candidatos de uma escrita por referência: as exatas, ou, sem nenhuma exata, as parciais.
    internal static IReadOnlyList<OcorrenciaNaPlanilhaDto> Candidatos(IReadOnlyList<Encontrada> encontradas) =>
        (encontradas.Any(e => e.Exata) ? encontradas.Where(e => e.Exata) : encontradas).Select(e => e.Ocorrencia).ToArray();

    internal static string Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return string.Empty;
        var decomposto = texto.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var resultado = new StringBuilder(decomposto.Length);
        var espaco = false;
        foreach (var caractere in decomposto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caractere) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(caractere))
            {
                if (!espaco) resultado.Append(' ');
                espaco = true;
                continue;
            }
            espaco = false;
            resultado.Append(caractere);
        }
        return resultado.ToString().Normalize(NormalizationForm.FormC);
    }

    private static OcorrenciaNaPlanilhaDto Ocorrencia(ValoresLidos fonte, int i, int j)
    {
        var linha = fonte.Origem.Linha + i;
        var contexto = fonte.Linhas[i]
            .Select((valor, k) => (valor, coluna: fonte.Origem.Coluna + k))
            .Where(celula => !string.IsNullOrWhiteSpace(celula.valor))
            .Take(MaximoDeCelulasNaLinha)
            .Select(celula => Celula(linha, celula.coluna, celula.valor))
            .ToArray();
        return new OcorrenciaNaPlanilhaDto
        {
            Aba = fonte.Origem.Aba ?? string.Empty,
            Celula = Celula(linha, fonte.Origem.Coluna + j, fonte.Linhas[i][j]),
            Linha = contexto
        };
    }

    private static CelulaDaPlanilhaDto Celula(int linha, int coluna, string valor) => new()
    {
        Endereco = IntervaloA1.Endereco(linha, coluna),
        Linha = linha,
        Coluna = coluna,
        ValorExibido = valor
    };
}
