using System.Globalization;

namespace Dante.Application.Planilhas;

// Valor bruto de uma célula, sem formatação. Na escrita, Texto é interpretado como digitado por um usuário na
// localidade da planilha ("129,90" vira número em pt_BR, "=A1*2" vira fórmula); Numero e Booleano são gravados como
// tais, sem depender da localidade.
public sealed record ValorDeCelula
{
    public static readonly ValorDeCelula Vazio = new() { Tipo = TipoDeValorDaCelula.Vazio };

    public TipoDeValorDaCelula Tipo { get; init; }
    public string? Texto { get; init; }
    public double? Numero { get; init; }
    public bool? Booleano { get; init; }

    public static ValorDeCelula DeTexto(string texto) => texto.Length == 0 ? Vazio :
        new() { Tipo = TipoDeValorDaCelula.Texto, Texto = texto };

    public static ValorDeCelula DeNumero(double numero) => new() { Tipo = TipoDeValorDaCelula.Numero, Numero = numero };

    public static ValorDeCelula DeBooleano(bool valor) => new() { Tipo = TipoDeValorDaCelula.Booleano, Booleano = valor };

    public static ValorDeCelula DeErro(string erro) => new() { Tipo = TipoDeValorDaCelula.Erro, Texto = erro };

    public bool EstaVazio => Tipo == TipoDeValorDaCelula.Vazio;

    public override string ToString() => Tipo switch
    {
        TipoDeValorDaCelula.Vazio => string.Empty,
        TipoDeValorDaCelula.Numero => Numero!.Value.ToString("R", CultureInfo.InvariantCulture),
        TipoDeValorDaCelula.Booleano => Booleano!.Value ? "TRUE" : "FALSE",
        _ => Texto ?? string.Empty
    };
}
