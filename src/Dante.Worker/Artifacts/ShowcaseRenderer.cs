using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dante.Worker.Attachments;

namespace Dante.Worker.Artifacts;

// A print of the conversation, already checked as an image by the attachment store.
public sealed record ShowcaseImage(string Id, string Path, int Width, int Height);

// Why the image could not be made, in words the user can act on.
public sealed class ShowcaseRenderException(string message) : Exception(message);

public interface IShowcaseRenderer
{
    // Null when the image can be made here; otherwise what is missing and how to install it.
    string? Unavailable();

    Task RenderAsync(ShowcaseSpec spec, IReadOnlyList<ShowcaseImage> prints, string outputPath,
        CancellationToken cancellationToken);
}

public sealed record ShowcaseText(string Text, int X, int Y, int Size, bool Bold, string Color, bool Centered = false);

public sealed record ShowcaseCard(ShowcaseImage Image, int X, int Y, int Width, int Height, int Radius);

public sealed record ShowcasePill(int X, int Y, int Width, int Height, string Background, ShowcaseText Label);

public sealed record ShowcaseLayout(int Width, int Height, IReadOnlyList<ShowcaseText> Texts,
    IReadOnlyList<ShowcaseCard> Cards, IReadOnlyList<ShowcasePill> Pills);

// Where everything goes (#98): title and subtitle on top, the prints in rows of cards at a common height, a label
// under each one and the footer at the bottom. Rows are chosen to make the prints as large as the canvas allows, so
// narrow phone prints go side by side and wide desktop prints stack when the canvas is tall.
public static class ShowcaseLayoutBuilder
{
    public const string Muted = "#4B5563";
    public const string HighlightBackground = "#111111";
    public const string HighlightText = "#FFD54A";

    public static (int Width, int Height) Canvas(ShowcaseFormat format) => format switch
    {
        ShowcaseFormat.Square => (1400, 1400),
        ShowcaseFormat.Portrait => (1200, 1500),
        _ => (1600, 900)
    };

    public static ShowcaseLayout Build(ShowcaseSpec spec, IReadOnlyList<ShowcaseImage> prints)
    {
        var (width, height) = Canvas(spec.Format);
        var unit = (width + height) / 2.0;
        var margin = (int)(0.075 * unit);
        var texts = new List<ShowcaseText>();

        var titleSize = (int)(0.044 * unit);
        var titleLines = Wrap(spec.Title, width - 2 * margin, titleSize, bold: true);
        while (titleLines.Count > 2 && titleSize > 0.032 * unit)
            titleLines = Wrap(spec.Title, width - 2 * margin, titleSize = (int)(titleSize * 0.9), bold: true);
        texts.AddRange(Lines(titleLines, margin, margin, titleSize, true, spec.Accent));
        var y = margin + Block(titleLines.Count, titleSize);
        if (spec.Subtitle.Length > 0)
        {
            var size = (int)(0.021 * unit);
            var lines = Wrap(spec.Subtitle, width - 2 * margin, size, bold: false);
            y += (int)(0.35 * titleSize);
            texts.AddRange(Lines(lines, margin, y, size, false, Muted));
            y += Block(lines.Count, size);
        }

        var labelSize = (int)(0.0175 * unit);
        var bottom = height - (int)(0.8 * margin);
        if (spec.Footer.Length > 0)
        {
            var footerY = bottom - labelSize;
            texts.Add(new ShowcaseText(spec.Footer, width / 2, footerY, labelSize, true, spec.Accent, Centered: true));
            bottom = footerY - (int)(0.035 * unit);
        }

        var top = y + (int)(0.05 * unit);
        var gap = (int)(0.035 * unit);
        var pillHeight = spec.Prints.Any(print => print.Label.Length > 0) ? labelSize * 2 : 0;
        var below = pillHeight == 0 ? 0 : pillHeight + (int)(0.018 * unit);
        var images = spec.Prints.Select(print => prints.Single(image => image.Id == print.Id)).ToArray();
        var (rows, printHeight) = Arrange(images, width - 2 * margin, bottom - top, gap, below);

        var cards = new List<ShowcaseCard>();
        var pills = new List<ShowcasePill>();
        var blockHeight = rows.Count * (printHeight + below) + (rows.Count - 1) * gap;
        var rowY = top + Math.Max(0, (bottom - top - blockHeight) / 2);
        var index = 0;
        foreach (var row in rows)
        {
            var widths = row.Select(image => (int)Math.Round(printHeight * (double)image.Width / image.Height)).ToArray();
            var x = (width - widths.Sum() - (row.Count - 1) * gap) / 2;
            for (var column = 0; column < row.Count; column++, index++)
            {
                var cardWidth = widths[column];
                var radius = Math.Clamp((int)(Math.Min(cardWidth, printHeight) * 0.035), 8, 22);
                cards.Add(new ShowcaseCard(row[column], x, rowY, cardWidth, printHeight, radius));
                var print = spec.Prints[index];
                if (print.Label.Length > 0)
                {
                    var pillWidth = Estimate(print.Label, labelSize, bold: true) + (int)(1.6 * labelSize);
                    var center = x + cardWidth / 2;
                    pills.Add(new ShowcasePill(center - pillWidth / 2, rowY + printHeight + below - pillHeight,
                        pillWidth, pillHeight, print.Highlight ? HighlightBackground : "#FFFFFF",
                        new ShowcaseText(print.Label, center, 0, labelSize, true,
                            print.Highlight ? HighlightText : spec.Accent, Centered: true)));
                }
                x += cardWidth + gap;
            }
            rowY += printHeight + below + gap;
        }
        return new ShowcaseLayout(width, height, texts, cards, pills);
    }

    // Every split of the prints, in order, into rows of equal size (the last one shorter): the one where the common
    // height, limited by the width of each row and the height of the area, gives the largest prints.
    private static (IReadOnlyList<IReadOnlyList<ShowcaseImage>> Rows, int Height) Arrange(ShowcaseImage[] images,
        int areaWidth, int areaHeight, int gap, int below)
    {
        IReadOnlyList<IReadOnlyList<ShowcaseImage>> best = [images];
        var bestHeight = 0;
        var bestArea = -1.0;
        for (var count = 1; count <= images.Length; count++)
        {
            var perRow = (int)Math.Ceiling(images.Length / (double)count);
            var rows = images.Chunk(perRow).Select(row => (IReadOnlyList<ShowcaseImage>)row).ToArray();
            var height = (areaHeight - (rows.Length - 1) * gap) / (double)rows.Length - below;
            foreach (var row in rows)
                height = Math.Min(height, (areaWidth - (row.Count - 1) * gap) /
                    row.Sum(image => (double)image.Width / image.Height));
            var area = images.Sum(image => height * height * image.Width / image.Height);
            if (height > 0 && area > bestArea) (best, bestHeight, bestArea) = (rows, (int)height, area);
        }
        if (bestHeight < 40) throw new ShowcaseRenderException("não há espaço para os prints nesse formato");
        return (best, bestHeight);
    }

    private static int Block(int lines, int size) => (int)(lines * size * 1.25);

    // One text per line: drawtext would draw the line break itself as a glyph.
    private static IEnumerable<ShowcaseText> Lines(IReadOnlyList<string> lines, int x, int y, int size, bool bold,
        string color) => lines.Select((line, index) => new ShowcaseText(line, x, y + (int)(index * size * 1.25), size,
            bold, color));

    // No font metrics here: an average glyph width per weight, a little generous, so lines break before overflowing.
    private static int Estimate(string text, int size, bool bold) => (int)(text.Length * size * (bold ? 0.6 : 0.53));

    private static List<string> Wrap(string text, int width, int size, bool bold)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && Estimate(line + " " + word, size, bold) > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            line.Append(line.Length > 0 ? " " : "").Append(word);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return lines;
    }
}

// Draws the layout with ffmpeg (#98): a light gradient, each print scaled and pasted with rounded corners and a soft
// shadow, rounded labels and the texts. The prints are never redrawn. Texts go through files (no escaping of what the
// agent wrote), the program name is fixed and resolved in absolute PATH entries, no shell, minimal environment.
public sealed class ShowcaseRenderer : IShowcaseRenderer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly (string Bold, string Regular)[] FontCandidates =
    [
        ("/usr/share/fonts/opentype/inter/Inter-Bold.otf", "/usr/share/fonts/opentype/inter/Inter-Regular.otf"),
        ("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
    ];

    public string? Unavailable()
    {
        if (Resolve("ffmpeg") is null) return "ffmpeg não encontrado no PATH; instale o ffmpeg (sudo apt install ffmpeg)";
        return Fonts() is null ? "nenhuma fonte encontrada; instale a Inter (sudo apt install fonts-inter)" : null;
    }

    public async Task RenderAsync(ShowcaseSpec spec, IReadOnlyList<ShowcaseImage> prints, string outputPath,
        CancellationToken cancellationToken)
    {
        if (Unavailable() is { } missing) throw new ShowcaseRenderException(missing);
        var layout = ShowcaseLayoutBuilder.Build(spec, prints);
        var parts = Directory.CreateDirectory(outputPath + ".parts").FullName;
        try
        {
            var arguments = Arguments(layout, Fonts()!.Value, parts, outputPath);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try { await RunAsync(Resolve("ffmpeg")!, arguments, timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ShowcaseRenderException($"a montagem passou de {Timeout.TotalSeconds:0} s");
            }
            if (!File.Exists(outputPath) || ImageInspector.Inspect(outputPath) is not { MediaType: "image/png" })
                throw new ShowcaseRenderException("o ffmpeg não produziu um PNG válido");
        }
        catch
        {
            File.Delete(outputPath);
            throw;
        }
        finally
        {
            Directory.Delete(parts, true);
        }
    }

    // The ffmpeg arguments for a layout; texts are written to files under parts. Public for the tests.
    public static IReadOnlyList<string> Arguments(ShowcaseLayout layout, (string Bold, string Regular) fonts,
        string parts, string outputPath)
    {
        var arguments = new List<string>
        {
            "-nostdin", "-v", "error", "-f", "lavfi", "-i",
            $"gradients=s={layout.Width}x{layout.Height}:c0=0xF6F8F7:c1=0xE4ECE8:x0=0:y0=0:x1={layout.Width}:y1={layout.Height}:d=1"
        };
        foreach (var card in layout.Cards)
            arguments.AddRange(["-protocol_whitelist", "file", "-i", card.Image.Path]);

        var graph = new StringBuilder();
        var current = "[0:v]";
        for (var index = 0; index < layout.Cards.Count; index++)
        {
            var card = layout.Cards[index];
            var blur = Math.Max(8, card.Radius);
            graph.Append($"[{index + 1}:v]scale={card.Width}:{card.Height}:flags=lanczos,format=rgba,")
                .Append($"geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='{Rounded(card.Radius)}',split[c{index}][m{index}];")
                .Append($"[m{index}]geq=r=0:g=0:b=0:a='alpha(X,Y)*0.28',pad=iw+{2 * blur}:ih+{2 * blur}:{blur}:{blur}:")
                .Append($"color=black@0,boxblur={blur / 2}:2[s{index}];")
                .Append($"{current}[s{index}]overlay={card.X - blur}:{card.Y - blur + blur / 2}[b{index}s];")
                .Append($"[b{index}s][c{index}]overlay={card.X}:{card.Y}[b{index}];");
            current = $"[b{index}]";
        }
        for (var index = 0; index < layout.Pills.Count; index++)
        {
            var pill = layout.Pills[index];
            graph.Append($"color=c=0x{pill.Background[1..]}:s={pill.Width}x{pill.Height}:d=1,format=rgba,")
                .Append($"geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='{Rounded(pill.Height / 2)}'[p{index}];")
                .Append($"{current}[p{index}]overlay={pill.X}:{pill.Y}[q{index}];");
            current = $"[q{index}]";
        }

        var texts = layout.Texts.Concat(layout.Pills.Select(pill => pill.Label with
        {
            Y = pill.Y + (pill.Height - pill.Label.Size) / 2 + pill.Label.Size / 12
        })).ToArray();
        graph.Append(current);
        for (var index = 0; index < texts.Length; index++)
        {
            var text = texts[index];
            var file = Path.Combine(parts, $"t{index}.txt");
            File.WriteAllText(file, text.Text, new UTF8Encoding(false));
            graph.Append(index == 0 ? "" : ",")
                .Append($"drawtext=fontfile={Safe(text.Bold ? fonts.Bold : fonts.Regular)}:textfile={Safe(file)}:")
                .Append($"expansion=none:fontsize={text.Size}:fontcolor=0x{text.Color[1..]}:")
                .Append(text.Centered ? $"x={text.X}-text_w/2:y={text.Y}" : $"x={text.X}:y={text.Y}");
        }
        if (texts.Length == 0) graph.Append("null");
        arguments.AddRange(["-filter_complex", graph.ToString(), "-frames:v", "1", "-f", "image2", "-c:v", "png",
            "-y", outputPath]);
        return arguments;
    }

    // Alpha of a rectangle with rounded corners of radius r: transparent outside the arc in each corner.
    private static string Rounded(int r) =>
        $"if(gt(abs(W/2-X),W/2-{r})*gt(abs(H/2-Y),H/2-{r}),if(lte(hypot({r}-(W/2-abs(W/2-X)),{r}-(H/2-abs(H/2-Y))),{r}),255,0),255)";

    // Paths inside the filter graph are D.A.N.T.E.'s own (fonts, text files); anything that would need escaping is
    // refused instead of escaped.
    private static string Safe(string path) =>
        path.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '_' or '-')
            ? path : throw new ShowcaseRenderException("caminho com caracteres não suportados para a montagem");

    private static (string Bold, string Regular)? Fonts()
    {
        foreach (var candidate in FontCandidates)
            if (File.Exists(candidate.Bold) && File.Exists(candidate.Regular)) return candidate;
        return null;
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };
        var inherited = new Dictionary<string, string?>(start.Environment, StringComparer.Ordinal);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "HOME", "LANG", "LC_ALL", "TMPDIR" })
            if (inherited.TryGetValue(name, out var value) && value is not null)
                start.Environment[name] = value;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new ShowcaseRenderException("o ffmpeg não pôde ser iniciado");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new ShowcaseRenderException("o ffmpeg não pôde ser iniciado");
        }
        process.StandardInput.Close();
        // Both streams are drained so ffmpeg never blocks on a full pipe; their content is not shown to anyone.
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* Already exited. */ }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new ShowcaseRenderException(
                $"o ffmpeg terminou com código {process.ExitCode.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string? Resolve(string tool)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // An empty or relative PATH entry would make the current directory executable.
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? tool + ".exe" : tool);
            if (File.Exists(candidate) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(candidate) &
                    (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0))
                return candidate;
        }
        return null;
    }
}
