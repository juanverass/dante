using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dante.Application.Planilhas;

// Coordenadas determinísticas da escrita (#224): aba opcional, célula ou retângulo em notação A1, ou a aba inteira.
// Linhas e colunas começam em 1. Colunas ou linhas abertas (A:C, 2:5) não são aceitas: toda leitura e escrita nomeia
// um retângulo finito ou a aba inteira, cuja leitura é limitada pela Application.
public sealed record IntervaloA1
{
    public const int MaximoDeLinhas = 10_000_000;
    public const int MaximoDeColunas = 18_278; // ZZZ
    private static readonly Regex RetanguloA1 =
        new(@"^\$?([A-Za-z]{1,3})\$?(\d{1,8})(?::\$?([A-Za-z]{1,3})\$?(\d{1,8}))?$", RegexOptions.Compiled);

    private IntervaloA1(string? aba, int linha, int coluna, int linhaFinal, int colunaFinal, bool abaInteira)
    {
        Aba = aba;
        Linha = linha;
        Coluna = coluna;
        LinhaFinal = linhaFinal;
        ColunaFinal = colunaFinal;
        AbaInteira = abaInteira;
    }

    public string? Aba { get; }
    public int Linha { get; }
    public int Coluna { get; }
    public int LinhaFinal { get; }
    public int ColunaFinal { get; }
    public bool AbaInteira { get; }

    public int QuantidadeDeLinhas => LinhaFinal - Linha + 1;
    public int QuantidadeDeColunas => ColunaFinal - Coluna + 1;
    public long QuantidadeDeCelulas => AbaInteira ? long.MaxValue : (long)QuantidadeDeLinhas * QuantidadeDeColunas;
    public bool CelulaUnica => !AbaInteira && Linha == LinhaFinal && Coluna == ColunaFinal;

    // A1 sem a aba: "B12" ou "B12:D20"; vazio quando é a aba inteira.
    public string Celulas => AbaInteira ? string.Empty :
        CelulaUnica ? Endereco(Linha, Coluna) : $"{Endereco(Linha, Coluna)}:{Endereco(LinhaFinal, ColunaFinal)}";

    public static IntervaloA1 DaAba(string aba)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aba);
        return new IntervaloA1(aba, 1, 1, MaximoDeLinhas, MaximoDeColunas, true);
    }

    public static IntervaloA1 DaCelula(string? aba, int linha, int coluna) => Retangulo(aba, linha, coluna, linha, coluna);

    public static IntervaloA1 Retangulo(string? aba, int linha, int coluna, int linhaFinal, int colunaFinal)
    {
        if (linha < 1 || coluna < 1 || linhaFinal < linha || colunaFinal < coluna || linhaFinal > MaximoDeLinhas ||
            colunaFinal > MaximoDeColunas)
            throw new ArgumentOutOfRangeException(nameof(linha), "Retângulo A1 inválido.");
        return new IntervaloA1(string.IsNullOrWhiteSpace(aba) ? null : aba, linha, coluna, linhaFinal, colunaFinal, false);
    }

    public IntervaloA1 NaAba(string aba) => new(aba, Linha, Coluna, LinhaFinal, ColunaFinal, AbaInteira);

    public bool Contem(int linha, int coluna) =>
        linha >= Linha && linha <= LinhaFinal && coluna >= Coluna && coluna <= ColunaFinal;

    public bool Intersecta(IntervaloA1 outro) =>
        Linha <= outro.LinhaFinal && outro.Linha <= LinhaFinal && Coluna <= outro.ColunaFinal && outro.Coluna <= ColunaFinal;

    // Forma aceita pelas APIs de planilha: aba entre aspas simples (aspas internas duplicadas) e o retângulo.
    public override string ToString()
    {
        var aba = Aba is null ? null : "'" + Aba.Replace("'", "''", StringComparison.Ordinal) + "'";
        return (aba, AbaInteira) switch
        {
            (null, _) => Celulas,
            (_, true) => aba,
            _ => $"{aba}!{Celulas}"
        };
    }

    public static IntervaloA1 Interpretar(string texto) => TentarInterpretar(texto, out var intervalo)
        ? intervalo!
        : throw new ArgumentException(
            $"Intervalo A1 inválido: {texto}. Use B12, B12:D20, Aba!B12:D20, 'Aba com espaço'!A1 ou só o nome da aba.",
            nameof(texto));

    public static bool TentarInterpretar(string? texto, out IntervaloA1? intervalo)
    {
        intervalo = null;
        if (string.IsNullOrWhiteSpace(texto) || texto.Length > 300) return false;
        texto = texto.Trim();
        string? aba = null;
        string celulas;
        if (texto.StartsWith('\''))
        {
            var fim = FimDaAbaEntreAspas(texto);
            if (fim < 0) return false;
            aba = texto[1..fim].Replace("''", "'", StringComparison.Ordinal);
            var resto = texto[(fim + 1)..];
            if (resto.Length == 0)
            {
                if (aba.Length == 0) return false;
                intervalo = DaAba(aba);
                return true;
            }
            if (resto[0] != '!') return false;
            celulas = resto[1..];
        }
        else if (texto.LastIndexOf('!') is var separador and >= 0)
        {
            aba = texto[..separador];
            celulas = texto[(separador + 1)..];
        }
        else if (RetanguloA1.IsMatch(texto))
        {
            celulas = texto;
        }
        else
        {
            // Sem "!" e sem forma de célula: é o nome de uma aba.
            intervalo = DaAba(texto);
            return true;
        }

        if (aba is not null && aba.Trim().Length == 0) return false;
        var match = RetanguloA1.Match(celulas.Trim());
        if (!match.Success) return false;
        var (linha, coluna) = (NumeroDaLinha(match.Groups[2].Value), IndiceDaColuna(match.Groups[1].Value));
        var (linhaFinal, colunaFinal) = match.Groups[3].Success
            ? (NumeroDaLinha(match.Groups[4].Value), IndiceDaColuna(match.Groups[3].Value))
            : (linha, coluna);
        // B5:A1 é o mesmo retângulo de A1:B5.
        (linha, linhaFinal) = (Math.Min(linha, linhaFinal), Math.Max(linha, linhaFinal));
        (coluna, colunaFinal) = (Math.Min(coluna, colunaFinal), Math.Max(coluna, colunaFinal));
        if (linha < 1 || coluna < 1 || linhaFinal > MaximoDeLinhas || colunaFinal > MaximoDeColunas) return false;
        intervalo = new IntervaloA1(aba, linha, coluna, linhaFinal, colunaFinal, false);
        return true;
    }

    public static string Endereco(int linha, int coluna) => NomeDaColuna(coluna) + linha.ToString(CultureInfo.InvariantCulture);

    public static string NomeDaColuna(int coluna)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(coluna, 1);
        var nome = new StringBuilder();
        for (var resto = coluna; resto > 0; resto = (resto - 1) / 26)
            nome.Insert(0, (char)('A' + (resto - 1) % 26));
        return nome.ToString();
    }

    public static int IndiceDaColuna(string nome)
    {
        if (string.IsNullOrEmpty(nome) || nome.Length > 3 || !nome.All(char.IsAsciiLetter))
            throw new ArgumentException($"Coluna inválida: {nome}.", nameof(nome));
        return nome.ToUpperInvariant().Aggregate(0, (indice, letra) => indice * 26 + letra - 'A' + 1);
    }

    private static int NumeroDaLinha(string texto) =>
        int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var linha) ? linha : -1;

    private static int FimDaAbaEntreAspas(string texto)
    {
        for (var i = 1; i < texto.Length; i++)
        {
            if (texto[i] != '\'') continue;
            if (i + 1 < texto.Length && texto[i + 1] == '\'') { i++; continue; }
            return i;
        }
        return -1;
    }

}
