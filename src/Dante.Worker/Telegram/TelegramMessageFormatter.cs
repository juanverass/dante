using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Dante.Worker.Telegram;

public sealed record TelegramFormattedMessage(string Html, string PlainText)
{
    public TelegramFormattedMessage WithPrefix(string prefix) =>
        new(WebUtility.HtmlEncode(prefix) + Html, prefix + PlainText);
}

// Fences may span batches. Every returned part is self-contained HTML, even inside a still-open Markdown block.
internal sealed partial class TelegramMessageFormatter
{
    private char fenceCharacter;
    private int fenceLength;
    private string? language;
    private bool atLineStart = true;

    public IReadOnlyList<TelegramFormattedMessage> Format(string text, int prefixReserve = 0)
    {
        var segments = new List<Segment>();
        var content = new StringBuilder();
        var code = fenceLength > 0;
        var segmentLanguage = language;
        for (var offset = 0; offset < text.Length;)
        {
            var newline = text.IndexOf('\n', offset);
            var end = newline < 0 ? text.Length : newline + 1;
            var line = text[offset..end];
            var match = atLineStart ? Fence().Match(line.TrimEnd('\r', '\n')) : Match.Empty;
            var delimiter = match.Success ? match.Groups[1].Value : "";
            var info = match.Success ? match.Groups[2].Value.Trim() : "";
            var opening = fenceLength == 0 && match.Success &&
                (delimiter[0] != '`' || !info.Contains('`'));
            var closing = fenceLength > 0 && match.Success && delimiter[0] == fenceCharacter &&
                delimiter.Length >= fenceLength && info.Length == 0;
            if (opening || closing)
            {
                if (content.Length > 0) segments.Add(new(content.ToString(), code, segmentLanguage));
                content.Clear();
                fenceLength = opening ? delimiter.Length : 0;
                fenceCharacter = delimiter[0];
                language = opening ? Language(info) : null;
                code = opening;
                segmentLanguage = language;
            }
            else content.Append(line);
            atLineStart = line.EndsWith('\n');
            offset = end;
        }
        if (content.Length > 0) segments.Add(new(content.ToString(), code, segmentLanguage));
        return Split(segments, 4000 - prefixReserve);
    }

    // Commands are trusted as a type, never as markup. Their content does not go through Markdown fence detection.
    public static IReadOnlyList<TelegramFormattedMessage> Command(string text, int prefixReserve = 0) =>
        Split([new Segment("→ Executando comando\n", false, null), new Segment(text + "\n", true, "bash")],
            4000 - prefixReserve);

    private static IReadOnlyList<TelegramFormattedMessage> Split(IEnumerable<Segment> segments, int limit)
    {
        var parts = new List<TelegramFormattedMessage>();
        var html = new StringBuilder();
        var plain = new StringBuilder();
        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(plain.ToString())) parts.Add(new(html.ToString(), plain.ToString()));
            html.Clear();
            plain.Clear();
        }
        foreach (var segment in segments)
        {
            var open = !segment.Code ? "" : segment.Language is null ? "<pre><code>" :
                $"<pre><code class=\"language-{segment.Language}\">";
            var close = segment.Code ? "</code></pre>" : "";
            var started = false;
            var offset = 0;
            var lineStart = true;
            foreach (var rune in segment.Text.EnumerateRunes())
            {
                var raw = rune.ToString();
                if (lineStart)
                {
                    var newline = segment.Text.IndexOf('\n', offset);
                    var end = newline < 0 ? segment.Text.Length : newline + 1;
                    var lineLength = WebUtility.HtmlEncode(segment.Text[offset..end]).Length;
                    var overheadForLine = close.Length + (started ? 0 : open.Length);
                    // Keep normal-sized lines whole; only a line longer than a fresh part is split between runes.
                    if (lineLength + open.Length + close.Length <= limit &&
                        html.Length + lineLength + overheadForLine > limit)
                    {
                        if (started) html.Append(close);
                        Flush();
                        started = false;
                    }
                }
                offset += raw.Length;
                lineStart = raw == "\n";
                if (!segment.Code && plain.Length == 0 && raw is "\n" or "\r") continue;
                // No dangling entities, tags or UTF-16 surrogate pairs at the boundary.
                var escaped = WebUtility.HtmlEncode(raw);
                var overhead = close.Length + (started ? 0 : open.Length);
                if (html.Length + escaped.Length + overhead > limit)
                {
                    if (started) html.Append(close);
                    Flush();
                    started = false;
                }
                if (!started) html.Append(open);
                html.Append(escaped);
                plain.Append(raw);
                started = true;
            }
            if (started) html.Append(close);
        }
        Flush();
        return parts;
    }

    private static string? Language(string info)
    {
        var name = info.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        name = name switch { "cs" or "c#" => "csharp", "py" => "python", "sh" or "shell" or "shellscript" => "bash",
            "patch" => "diff", _ => name };
        return name is not null && name.Length <= 32 && LanguageName().IsMatch(name) ? name : null;
    }

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})(.*)$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^[a-z0-9_+\-]+$")]
    private static partial Regex LanguageName();

    private sealed record Segment(string Text, bool Code, string? Language);
}
