using System.Globalization;

namespace Dante.Worker.Telegram;

// Botão de decisão de uma ação protegida (AD-72, #311, PR 2): "ac:<id>:<hash curto>:a" aprova o payload cujo hash a
// mensagem exibiu; "ac:<id>:r" recusa. Só transporta o pedido e o hash mostrado: quem decide se vale (dono, prazo, hash,
// revogação, decisão única) é a Application. Cabe nos 64 bytes do callback_data do Telegram.
internal sealed record TelegramAcaoCallback(Guid Id, bool Aprovar, string? Hash)
{
    private const string Prefixo = "ac";

    public static string Aprovacao(Guid id, string hashCurto) =>
        $"{Prefixo}:{id.ToString("N", CultureInfo.InvariantCulture)}:{hashCurto}:a";

    public static string Recusa(Guid id) => $"{Prefixo}:{id.ToString("N", CultureInfo.InvariantCulture)}:r";

    public static bool TryParse(string? data, out TelegramAcaoCallback acao)
    {
        acao = null!;
        if (data is null || data.Length > 64) return false;
        var partes = data.Split(':');
        if (partes.Length < 3 || partes[0] != Prefixo ||
            !Guid.TryParseExact(partes[1], "N", out var id))
            return false;
        if (partes is [_, _, "r"])
        {
            acao = new(id, false, null);
            return true;
        }
        if (partes is [_, _, var hash, "a"] && hash.Length == Dante.Application.Execucoes.AprovacaoDeAcaoDto.TamanhoDoHashCurto &&
            hash.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0)
        {
            acao = new(id, true, hash);
            return true;
        }
        return false;
    }
}
