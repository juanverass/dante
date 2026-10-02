using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dante.Worker.Artifacts;

public enum ShowcaseFormat { Landscape, Square, Portrait }

public sealed record ShowcasePrint(string Id, string Label, bool Highlight);

// What the agent decides for a showcase image (#98): only text, colour and arrangement. The prints themselves are the
// user's files, referenced by id and pasted unchanged by the D.A.N.T.E.
public sealed partial record ShowcaseSpec(ShowcaseFormat Format, string Title, string Subtitle, string Accent,
    IReadOnlyList<ShowcasePrint> Prints, string Footer)
{
    public const int MaxPrints = 6;
    public const int MaxTitle = 70;
    public const int MaxSubtitle = 140;
    public const int MaxLabel = 24;
    public const int MaxFooter = 60;
    public const string DefaultAccent = "#1B4D3E";

    // The format the agent is asked for, shown in the turn sent to it.
    public const string Schema = """{"format":"landscape|square|portrait","title":"...","subtitle":"...","accent":"#RRGGBB","prints":[{"id":"A000001","label":"...","highlight":false}],"footer":"..."}""";

    // Reads the spec from the agent's reply: a ```json block, or else the outermost object in the text. Every field is
    // checked; the error says what to fix, so an adjustment can ask for it.
    public static (ShowcaseSpec? Spec, string? Error) Parse(string reply, IReadOnlyCollection<string> printIds)
    {
        var json = Fenced().Match(reply) is { Success: true } fenced ? fenced.Groups[1].Value
            : reply.IndexOf('{') is var start and >= 0 && reply.LastIndexOf('}') is var end && end > start
                ? reply[start..(end + 1)] : null;
        if (json is null) return (null, "a resposta do agente não trouxe o JSON da vitrine");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, "o JSON da vitrine não é um objeto");

            var format = Text(root, "format")?.ToLowerInvariant() switch
            {
                null or "" or "landscape" or "paisagem" => ShowcaseFormat.Landscape,
                "square" or "quadrado" => ShowcaseFormat.Square,
                "portrait" or "retrato" => ShowcaseFormat.Portrait,
                _ => (ShowcaseFormat?)null
            };
            if (format is null) return (null, "format deve ser landscape, square ou portrait");
            var title = Clean(Text(root, "title"));
            if (title.Length == 0) return (null, "title é obrigatório");
            if (title.Length > MaxTitle) return (null, $"title passa de {MaxTitle} caracteres");
            var subtitle = Clean(Text(root, "subtitle"));
            if (subtitle.Length > MaxSubtitle) return (null, $"subtitle passa de {MaxSubtitle} caracteres");
            var footer = Clean(Text(root, "footer"));
            if (footer.Length > MaxFooter) return (null, $"footer passa de {MaxFooter} caracteres");
            var accent = Text(root, "accent") is { Length: > 0 } color ? color.Trim() : DefaultAccent;
            if (!Color().IsMatch(accent)) return (null, "accent deve ser uma cor #RRGGBB");

            if (!root.TryGetProperty("prints", out var items) || items.ValueKind != JsonValueKind.Array)
                return (null, "prints é obrigatório");
            var prints = new List<ShowcasePrint>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return (null, "cada item de prints deve ser um objeto");
                var id = Text(item, "id")?.Trim().ToUpperInvariant() ?? "";
                if (!printIds.Contains(id)) return (null, $"o print {(id.Length == 0 ? "sem id" : id)} não está nesta conversa");
                if (prints.Any(print => print.Id == id)) return (null, $"o print {id} aparece duas vezes");
                var label = Clean(Text(item, "label"));
                if (label.Length > MaxLabel) return (null, $"a etiqueta de {id} passa de {MaxLabel} caracteres");
                var highlight = item.TryGetProperty("highlight", out var flag) && flag.ValueKind == JsonValueKind.True;
                prints.Add(new ShowcasePrint(id, label, highlight));
            }
            if (prints.Count == 0) return (null, "prints está vazio");
            if (prints.Count > MaxPrints) return (null, $"use no máximo {MaxPrints} prints por imagem");
            if (prints.Count(print => print.Highlight) > 1) return (null, "destaque no máximo uma etiqueta");

            return (new ShowcaseSpec(format.Value, title, subtitle, accent.ToUpperInvariant(), prints, footer), null);
        }
        catch (JsonException)
        {
            return (null, "o JSON da vitrine é inválido");
        }
    }

    public static string Name(ShowcaseFormat format) => format switch
    {
        ShowcaseFormat.Square => "quadrado",
        ShowcaseFormat.Portrait => "retrato",
        _ => "paisagem"
    };

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // One line of printable text: control characters (including line breaks) become spaces.
    private static string Clean(string? text) => text is null ? ""
        : Spaces().Replace(new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray()), " ")
            .Trim().Normalize(System.Text.NormalizationForm.FormC);

    [GeneratedRegex(@"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Fenced();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex Color();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
