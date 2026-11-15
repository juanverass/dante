using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Dante.Application.DocumentosFonte;

// Proveniência por trecho (#147): aponta a parte indexada de uma revisão da fonte e, opcionalmente, o intervalo citado.
// Base = brain://fonte/<id>/revisao/<rev>/parte/<n>?hash=<hash do documento>, a mesma referência de TrechoDaFonteDto.
// Intervalo = &inicio=<a>&fim=<b>&sha256=<hash do texto citado>: posições [a, b) em pontos de código (Rune) do documento
// inteiro, como o Inicio do índice, e SHA-256 do UTF-8 exato do texto citado. Sem intervalo, a citação é a parte inteira.
// Só a forma canônica é aceita: o texto relido precisa ser idêntico ao escrito, então variações (zeros à esquerda,
// maiúsculas no sha256, parâmetros fora de ordem ou extras) não são referência. O hash do documento só é comparado por
// igualdade com a referência do índice.
public sealed record ReferenciaDeTrecho(Guid IdDocumento, int Revisao, int Numero, string HashDoDocumento,
    int? Inicio = null, int? Fim = null, string? Sha256 = null)
{
    public string Base => $"brain://fonte/{IdDocumento:D}/revisao/{Revisao}/parte/{Numero}?hash={HashDoDocumento}";
    public string Texto => Inicio is { } a ? $"{Base}&inicio={a}&fim={Fim}&sha256={Sha256}" : Base;

    public static ReferenciaDeTrecho? Ler(string? texto)
    {
        if (texto is null) return null;
        var m = Formato.Match(texto);
        if (!m.Success || !Guid.TryParse(m.Groups["id"].Value, out var id) || id == Guid.Empty) return null;
        static int? Numero(Group g) => g.Success && int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        if (Numero(m.Groups["rev"]) is not { } revisao || Numero(m.Groups["n"]) is not { } numero) return null;
        var inicio = Numero(m.Groups["ini"]); var fim = Numero(m.Groups["fim"]);
        if (m.Groups["ini"].Success && (inicio is null || fim is null || inicio >= fim)) return null;
        var lida = new ReferenciaDeTrecho(id, revisao, numero, m.Groups["hash"].Value, inicio, fim, m.Groups["sha"].Success ? m.Groups["sha"].Value : null);
        return lida.Texto == texto ? lida : null;
    }

    public static string HashDoTexto(string texto) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(texto)));

    private static readonly Regex Formato = new(@"^brain://fonte/(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/revisao/(?<rev>[1-9][0-9]{0,8})/parte/(?<n>0|[1-9][0-9]{0,8})\?hash=(?<hash>[0-9A-Za-z]{1,128})(?:&inicio=(?<ini>0|[1-9][0-9]{0,8})&fim=(?<fim>[1-9][0-9]{0,8})&sha256=(?<sha>[0-9a-f]{64}))?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

// Resultado da fundamentação de uma captura num trecho: os três campos da proveniência do candidato.
public sealed record FundamentoNaFonteDto(string ReferenciaDaFonte, string HashDoDocumento, string TrechoDaFonte);
