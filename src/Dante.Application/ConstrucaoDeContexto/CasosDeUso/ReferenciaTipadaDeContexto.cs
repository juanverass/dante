using System.Globalization;

namespace Dante.Application.ConstrucaoDeContexto;

internal sealed record ReferenciaTipadaDeContexto(string Tipo, Guid Id, int Revisao)
{
    internal string Chave => $"{Tipo}:{Id:D}@{Revisao}";
    internal static ReferenciaTipadaDeContexto? Ler(string texto)
    {
        var separador = texto.IndexOf(':'); var arroba = texto.LastIndexOf('@');
        if (separador < 0 || arroba <= separador) return null;
        var tipo = texto[..separador];
        return tipo is "conhecimento" or "candidato" or "fonte" &&
            Guid.TryParse(texto[(separador + 1)..arroba], out var id) && id != Guid.Empty &&
            int.TryParse(texto[(arroba + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var revisao) && revisao > 0
            ? new(tipo, id, revisao) : null;
    }
}
