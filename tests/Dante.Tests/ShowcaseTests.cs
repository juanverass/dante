using Dante.Worker.Artifacts;

namespace Dante.Tests;

// Showcase images (#98): the agent's answer is validated field by field, the layout keeps every print whole and inside
// the canvas, and nothing the agent wrote reaches the ffmpeg command line.
public sealed class ShowcaseTests
{
    private static readonly string[] Ids = ["A000001", "A000002", "A000003", "A000004"];

    [Fact]
    public void AFencedAnswerBecomesTheSpec()
    {
        var (spec, error) = ShowcaseSpec.Parse("""
            Aqui está:
            ```json
            {"format":"square","title":"  No celular   também ","subtitle":"Menu acessível\ne alto contraste.",
             "accent":"#1b4d3e","prints":[{"id":"a000002","label":"Menu aberto"},{"id":"A000004","label":"Alto contraste","highlight":true}],
             "footer":"projeto-ong-html5.vercel.app"}
            ```
            """, Ids);

        Assert.Null(error);
        Assert.Equal(ShowcaseFormat.Square, spec!.Format);
        Assert.Equal(("No celular também", "Menu acessível e alto contraste.", "#1B4D3E", "projeto-ong-html5.vercel.app"),
            (spec.Title, spec.Subtitle, spec.Accent, spec.Footer));
        Assert.Equal([new ShowcasePrint("A000002", "Menu aberto", false), new ShowcasePrint("A000004", "Alto contraste", true)],
            spec.Prints);
    }

    [Fact]
    public void ABareObjectWithDefaultsIsAccepted()
    {
        var (spec, error) = ShowcaseSpec.Parse("""{"title":"Antes e depois","prints":[{"id":"A000001"}]}""", Ids);

        Assert.Null(error);
        Assert.Equal((ShowcaseFormat.Landscape, ShowcaseSpec.DefaultAccent, "", ""),
            (spec!.Format, spec.Accent, spec.Subtitle, spec.Footer));
        Assert.Equal("", Assert.Single(spec.Prints).Label);
    }

    [Theory]
    [InlineData("Não sei quais prints usar. Pode me dizer?", "a resposta do agente não trouxe o JSON da vitrine")]
    [InlineData("{\"title\":\"x\",", "a resposta do agente não trouxe o JSON da vitrine")]
    [InlineData("{\"title\":\"x\",\"prints\":[{\"id\":\"A000001\"}],}", "o JSON da vitrine é inválido")]
    [InlineData("{\"prints\":[{\"id\":\"A000001\"}]}", "title é obrigatório")]
    [InlineData("{\"title\":\"x\",\"format\":\"story\",\"prints\":[{\"id\":\"A000001\"}]}", "format deve ser landscape, square ou portrait")]
    [InlineData("{\"title\":\"x\",\"accent\":\"red\",\"prints\":[{\"id\":\"A000001\"}]}", "accent deve ser uma cor #RRGGBB")]
    [InlineData("{\"title\":\"x\",\"prints\":[]}", "prints está vazio")]
    [InlineData("{\"title\":\"x\",\"prints\":[{\"id\":\"/etc/passwd\"}]}", "o print /ETC/PASSWD não está nesta conversa")]
    [InlineData("{\"title\":\"x\",\"prints\":[{\"id\":\"A000001\"},{\"id\":\"A000001\"}]}", "o print A000001 aparece duas vezes")]
    [InlineData("{\"title\":\"x\",\"prints\":[{\"id\":\"A000001\",\"highlight\":true},{\"id\":\"A000002\",\"highlight\":true}]}",
        "destaque no máximo uma etiqueta")]
    [InlineData("{\"title\":\"x\",\"prints\":[{\"id\":\"A000001\",\"label\":\"uma etiqueta comprida demais aqui\"}]}",
        "a etiqueta de A000001 passa de 24 caracteres")]
    public void InvalidAnswersSayWhatToFix(string reply, string expected)
    {
        var (spec, error) = ShowcaseSpec.Parse(reply, Ids);

        Assert.Null(spec);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void LongTextsAreRefused()
    {
        var title = new string('a', ShowcaseSpec.MaxTitle + 1);

        Assert.Equal("title passa de 70 caracteres",
            ShowcaseSpec.Parse($$"""{"title":"{{title}}","prints":[{"id":"A000001"}]}""", Ids).Error);
        Assert.Equal("use no máximo 6 prints por imagem", ShowcaseSpec.Parse("{\"title\":\"x\",\"prints\":[" +
            string.Join(',', Enumerable.Range(1, 7).Select(index => $"{{\"id\":\"A00000{index}\"}}")) + "]}",
            Enumerable.Range(1, 7).Select(index => $"A00000{index}").ToArray()).Error);
    }

    [Fact]
    public void PhonePrintsGoSideBySideAtTheSameHeightWithTheirLabels()
    {
        var spec = Spec(ShowcaseFormat.Landscape, ["Início", "Menu aberto", "Cadastro", "Alto contraste"], highlight: 3);
        var prints = Ids.Select(id => new ShowcaseImage(id, $"/p/{id}.png", 390, 844)).ToArray();

        var layout = ShowcaseLayoutBuilder.Build(spec, prints);

        Assert.Equal((1600, 900), (layout.Width, layout.Height));
        Assert.Equal(Ids, layout.Cards.Select(card => card.Image.Id));
        Assert.Single(layout.Cards.Select(card => (card.Y, card.Height)).Distinct());
        Assert.True(layout.Cards.Zip(layout.Cards.Skip(1)).All(pair => pair.First.X + pair.First.Width < pair.Second.X));
        AssertInside(layout);
        foreach (var card in layout.Cards)
            Assert.Equal(390.0 / 844, card.Width / (double)card.Height, 2);
        Assert.Equal(["#FFFFFF", "#FFFFFF", "#FFFFFF", ShowcaseLayoutBuilder.HighlightBackground],
            layout.Pills.Select(pill => pill.Background));
        Assert.Equal(ShowcaseLayoutBuilder.HighlightText, layout.Pills[3].Label.Color);
        Assert.All(layout.Pills.Zip(layout.Cards), pair => Assert.True(pair.First.Y > pair.Second.Y + pair.Second.Height));
        var footer = Assert.Single(layout.Texts, text => text.Text == "site.example");
        Assert.True(footer.Centered && footer.Y > layout.Pills.Max(pill => pill.Y + pill.Height));
    }

    [Fact]
    public void WideDesktopPrintsStackOnATallCanvasAndSitSideBySideOnAWideOne()
    {
        var prints = Ids[..2].Select(id => new ShowcaseImage(id, $"/p/{id}.png", 1440, 900)).ToArray();

        var portrait = ShowcaseLayoutBuilder.Build(Spec(ShowcaseFormat.Portrait, ["Normal", "Contraste"]), prints);
        var landscape = ShowcaseLayoutBuilder.Build(Spec(ShowcaseFormat.Landscape, ["Normal", "Contraste"]), prints);

        Assert.Equal(portrait.Cards[0].X, portrait.Cards[1].X);
        Assert.True(portrait.Cards[1].Y > portrait.Cards[0].Y + portrait.Cards[0].Height);
        Assert.Equal(landscape.Cards[0].Y, landscape.Cards[1].Y);
        AssertInside(portrait);
        AssertInside(landscape);
    }

    [Fact]
    public void ALongTitleWrapsInsideTheCanvas()
    {
        var spec = Spec(ShowcaseFormat.Portrait, ["x"]) with { Title = "Mesmo site, dois modos de leitura para todo mundo ler" };

        var layout = ShowcaseLayoutBuilder.Build(spec, [new ShowcaseImage("A000001", "/p/a.png", 1440, 900)]);

        var title = layout.Texts.Where(text => text.Bold && !text.Centered).ToArray();
        Assert.InRange(title.Length, 2, 2);
        Assert.Equal(spec.Title, string.Join(' ', title.Select(text => text.Text)));
        AssertInside(layout);
    }

    [Fact]
    public void WhatTheAgentWroteGoesThroughFilesNeverIntoTheCommand()
    {
        var parts = Directory.CreateTempSubdirectory("dante-showcase-args-").FullName;
        try
        {
            var spec = Spec(ShowcaseFormat.Landscape, ["a'b:c,d;[e]"]) with { Title = "drawtext=textfile=/etc/passwd" };
            var layout = ShowcaseLayoutBuilder.Build(spec, [new ShowcaseImage("A000001", "/p/A000001.png", 390, 844)]);

            var arguments = ShowcaseRenderer.Arguments(layout, ("/fonts/B.ttf", "/fonts/R.ttf"), parts, "/out/v.png");

            Assert.DoesNotContain(arguments, argument => argument.Contains("/etc/passwd") || argument.Contains("a'b"));
            var files = Directory.EnumerateFiles(parts).Select(File.ReadAllText).ToArray();
            Assert.Contains("drawtext=textfile=/etc/passwd", files);
            Assert.Contains("a'b:c,d;[e]", files);
            Assert.Equal(["-protocol_whitelist", "file", "-i", "/p/A000001.png"], arguments.Skip(7).Take(4));
            Assert.Equal("/out/v.png", arguments[^1]);
            Assert.Throws<ShowcaseRenderException>(() =>
                ShowcaseRenderer.Arguments(layout, ("/fonts/B old.ttf", "/fonts/R.ttf"), parts, "/out/v.png"));
        }
        finally
        {
            Directory.Delete(parts, true);
        }
    }

    private static ShowcaseSpec Spec(ShowcaseFormat format, string[] labels, int highlight = -1) =>
        new(format, "No celular também", "Menu acessível pelo teclado, formulário com máscaras e alto contraste.",
            "#1B4D3E", labels.Select((label, index) => new ShowcasePrint(Ids[index], label, index == highlight)).ToArray(),
            "site.example");

    private static void AssertInside(ShowcaseLayout layout)
    {
        foreach (var card in layout.Cards)
        {
            Assert.InRange(card.X, 0, layout.Width - card.Width);
            Assert.InRange(card.Y, 0, layout.Height - card.Height);
        }
        foreach (var pill in layout.Pills)
            Assert.InRange(pill.Y + pill.Height, 0, layout.Height);
    }
}
