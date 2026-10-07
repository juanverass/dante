using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dante.Application.Planilhas;
using Dante.Infrastructure.Google;
using Dante.Infrastructure.Planilhas;
using Dante.Worker.Planilhas;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class GoogleDriveXlsxAdapterTests
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly OrigemDaSolicitacao Origem = new("mcp", "telegram:42", "codex");

    [Fact]
    public async Task XlsxNoDriveUsaAsMesmasFerramentasComCoordenadasBuscaECacheExplicito()
    {
        using var ambiente = new AmbienteXlsx();
        using var provider = new ServiceCollection().AddSingleton(ambiente.Servico).BuildServiceProvider();
        var mcp = new ServidorMcpDePlanilhas(provider.GetRequiredService<IServiceScopeFactory>(), Origem);
        var cadastro = await ambiente.Servico.CadastrarAsync("dados", "https://drive.google.com/file/d/" + GoogleSheetsFalso.IdPadrao + "/view");
        Assert.Equal("Dados.xlsx", cadastro.Titulo);
        var descricao = await ambiente.Servico.DescreverAsync("dados");
        var aba = Assert.Single(descricao.Planilha.Abas);
        Assert.Equal(("Dados", "A1:D4"), (aba.Titulo, aba.AreaUsada));
        Assert.Equal(["A3:B3"], aba.Mesclagens);
        var leitura = await ambiente.Servico.LerAsync("dados", "Dados!A1:D4");
        Assert.Equal(["A1", "B1", "C1", "D1", "A2", "B2", "A3", "D4"], leitura.Celulas.Select(c => c.Endereco));
        Assert.Equal("=B1*2", leitura.Celulas.Single(c => c.Endereco == "C1").Formula);
        Assert.Equal("25", leitura.Celulas.Single(c => c.Endereco == "C1").ValorExibido);
        Assert.Equal("A3:B3", leitura.Celulas.Single(c => c.Endereco == "A3").Mesclagem);
        var busca = await ambiente.Servico.BuscarAsync("dados", "internet");
        Assert.Equal(["A1", "A2"], busca.Ocorrencias.Select(o => o.Celula.Endereco));
        Assert.Contains("último resultado", busca.Observacao);
        var (texto, erro) = await ServidorMcpDePlanilhasTests.Chamar(mcp, "ler_intervalo", new JsonObject { ["planilha"] = "dados", ["intervalo"] = "Dados!C1" });
        Assert.False(erro, texto);
        Assert.Contains("último resultado", texto);
        Assert.Empty(ambiente.Drive.Uploads);
        Assert.DoesNotContain(ambiente.Drive.Requisicoes, r => r.Url.Contains("sheets.googleapis.com"));
    }

    [Fact]
    public async Task EscritaPorReferenciaPreservaMesmoArquivoEstilosFormulasPartesEAuditoria()
    {
        using var ambiente = new AmbienteXlsx();
        await ambiente.CadastrarAsync();
        var antes = Partes(ambiente.Drive.Conteudo);
        var resultado = await ambiente.Servico.AtualizarPorReferenciaAsync(new()
        {
            Planilha = "dados", Referencia = "internet", Coluna = "B", Valor = ValorDeCelula.DeNumero(119.9), ValorEsperado = "12.5"
        }, Origem);
        Assert.Equal("B1", Assert.Single(resultado.Celulas).Endereco);
        var upload = Assert.Single(ambiente.Drive.Uploads);
        Assert.Contains("/files/" + GoogleSheetsFalso.IdPadrao + "?uploadType=media", upload.Url);
        Assert.Contains("/upload/drive/v2/", upload.Url);
        Assert.Contains("convert=false", upload.Url);
        Assert.Equal("\"v1\"", upload.IfMatch);
        Assert.Equal(GoogleDriveXlsxAdapter.MimeXlsx, upload.Mime);
        var depois = Partes(ambiente.Drive.Conteudo);
        foreach (var parte in antes.Where(p => p.Key is not "xl/worksheets/sheet1.xml" and not "xl/workbook.xml"))
            Assert.Equal(parte.Value, depois[parte.Key]);
        var xmlAntes = Xml(antes["xl/worksheets/sheet1.xml"]);
        var xmlDepois = Xml(depois["xl/worksheets/sheet1.xml"]);
        var b1 = xmlDepois.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == "B1");
        Assert.Equal("1", (string?)b1.Attribute("s"));
        Assert.Equal("119.9", b1.Element(Ns + "v")!.Value);
        foreach (var cell in xmlAntes.Descendants(Ns + "c").Where(c => (string?)c.Attribute("r") != "B1"))
            Assert.True(XNode.DeepEquals(cell, xmlDepois.Descendants(Ns + "c").Single(c => (string?)c.Attribute("r") == (string?)cell.Attribute("r"))));
        Assert.Equal("1", (string?)Xml(depois["xl/workbook.xml"])
            .Descendants(Ns + "calcPr").Single().Attribute("forceFullCalc"));
        var auditoria = JsonNode.Parse(Assert.Single(ambiente.Auditoria.Linhas))!;
        Assert.Equal(("B1", "12.5", "119.9", "codex"), ((string?)auditoria["Endereco"], (string?)auditoria["ValorAnterior"],
            (string?)auditoria["ValorNovo"], (string?)auditoria["Agente"]));
    }

    [Theory]
    [InlineData("Dados!C1", false, MotivoDaFalhaDePlanilha.Conflito)]
    [InlineData("Dados!B3", false, MotivoDaFalhaDePlanilha.Invalida)]
    [InlineData("Dados!B1", true, MotivoDaFalhaDePlanilha.Conflito)]
    public async Task ProtecoesDaApplicationRecusamFormulaMesclagemEDivergencia(string intervalo, bool divergente,
        MotivoDaFalhaDePlanilha motivo)
    {
        using var ambiente = new AmbienteXlsx();
        await ambiente.CadastrarAsync();
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.Servico.AtualizarAsync(new()
        {
            Planilha = "dados", Alteracoes = [new() { Intervalo = intervalo, Valores = [[ValorDeCelula.DeNumero(3)]],
                ValoresEsperados = divergente ? [["999"]] : null }]
        }, Origem));
        Assert.Equal(motivo, falha.Motivo);
        Assert.Empty(ambiente.Drive.Uploads);
        Assert.Empty(ambiente.Auditoria.Linhas);
    }

    [Fact]
    public async Task AlvoAmbiguoNaoEscreve()
    {
        using var ambiente = new AmbienteXlsx();
        await ambiente.CadastrarAsync();
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.Servico.AtualizarPorReferenciaAsync(new()
        {
            Planilha = "dados", Referencia = "inter", Coluna = "B", Valor = ValorDeCelula.DeNumero(9), Intervalo = "Dados!A1:B2"
        }, Origem));
        Assert.Equal(MotivoDaFalhaDePlanilha.Ambigua, falha.Motivo);
        Assert.Empty(ambiente.Drive.Uploads);
    }

    [Theory]
    [InlineData("antes-da-escrita")]
    [InlineData("durante-download")]
    [InlineData("antes-upload")]
    [InlineData("durante-upload")]
    public async Task MudancaConcorrenteRecusaSobrescrita(string momento)
    {
        using var ambiente = new AmbienteXlsx();
        var arquivo = await ambiente.Adapter.ObterArquivoAsync(GoogleSheetsFalso.IdPadrao, default);
        var leitura = await ambiente.Adapter.LerAsync(arquivo, IntervaloA1.Interpretar("Dados!B1"), 1, default);
        if (momento == "antes-da-escrita") ambiente.Drive.Versao++;
        if (momento == "durante-download") ambiente.Drive.AlterarAoBaixar = true;
        if (momento == "antes-upload") ambiente.Drive.AlterarNaConsulta = ambiente.Drive.Consultas + 3;
        if (momento == "durante-upload") ambiente.Drive.AlterarAoEnviar = true;
        arquivo = await ambiente.Adapter.ObterArquivoAsync(GoogleSheetsFalso.IdPadrao, default);
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.Adapter.AtualizarAsync(arquivo,
            [new(IntervaloA1.Interpretar("Dados!B1"), [[ValorDeCelula.DeNumero(3)]], leitura.Revisao)], default));
        Assert.Equal(MotivoDaFalhaDePlanilha.Conflito, falha.Motivo);
        Assert.Equal("12.5", new DocumentoXlsx(ambiente.Drive.Conteudo).Ler(IntervaloA1.Interpretar("Dados!B1"), 1, "x").Celulas.Single().ValorExibido);
        Assert.Equal(momento == "durante-upload" ? 1 : 0, ambiente.Drive.Uploads.Count);
    }

    [Theory]
    [InlineData("sem-etag", MotivoDaFalhaDePlanilha.NaoSuportada)]
    [InlineData("etag-fraco", MotivoDaFalhaDePlanilha.NaoSuportada)]
    [InlineData("sem-edicao", MotivoDaFalhaDePlanilha.SemPermissao)]
    [InlineData("sem-download", MotivoDaFalhaDePlanilha.SemPermissao)]
    public async Task PermissoesERevisaoSaoObrigatorias(string caso, MotivoDaFalhaDePlanilha motivo)
    {
        using var ambiente = new AmbienteXlsx();
        await ambiente.CadastrarAsync();
        if (caso == "sem-etag") ambiente.Drive.TemETag = false;
        if (caso == "etag-fraco") ambiente.Drive.ETagFraco = true;
        if (caso == "sem-edicao") ambiente.Drive.PodeEditar = false;
        if (caso == "sem-download") ambiente.Drive.PodeBaixar = false;
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.Servico.AtualizarAsync(Escrita("Dados!B1", 4), Origem));
        Assert.Equal(motivo, falha.Motivo);
        Assert.Empty(ambiente.Drive.Uploads);
    }

    [Fact]
    public async Task AdicionarLinhaPreservaConteudoForaDoAlvoEOrdenacaoXml()
    {
        using var ambiente = new AmbienteXlsx();
        await ambiente.CadastrarAsync();
        var resultado = await ambiente.Servico.AdicionarLinhaAsync(new()
        {
            Planilha = "dados", Intervalo = "Dados!D1:D1", Valores = [ValorDeCelula.DeTexto("nova")]
        }, Origem);
        Assert.Equal("'Dados'!D2", Assert.Single(resultado.Intervalos));
        var documento = new DocumentoXlsx(ambiente.Drive.Conteudo);
        Assert.Equal("nova", documento.Ler(IntervaloA1.Interpretar("Dados!D2"), 1, "x").Celulas.Single().ValorExibido);
        Assert.Equal("fora", documento.Ler(IntervaloA1.Interpretar("Dados!D4"), 1, "x").Celulas.Single().ValorExibido);
        var xml = Xml(Partes(ambiente.Drive.Conteudo)["xl/worksheets/sheet1.xml"]);
        Assert.Equal(["A2", "B2", "D2"], xml.Descendants(Ns + "row").Single(r => (string?)r.Attribute("r") == "2")
            .Elements(Ns + "c").Select(c => (string?)c.Attribute("r")));
    }

    [Fact]
    public async Task UploadComFalhaNaoERepetidoEAuditoriaNaoDeclaraSucesso()
    {
        using var ambiente = new AmbienteXlsx();
        await ambiente.CadastrarAsync();
        ambiente.Drive.StatusDoUpload = HttpStatusCode.ServiceUnavailable;
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => ambiente.Servico.AtualizarAsync(Escrita("Dados!B1", 3), Origem));
        Assert.Contains("Confira o arquivo", falha.Message);
        Assert.Single(ambiente.Drive.Uploads);
        Assert.Empty(ambiente.Auditoria.Linhas);
    }

    [Fact]
    public async Task DriveRenova401ERepete429ApenasNaLeitura()
    {
        using var ambiente = new AmbienteXlsx();
        ambiente.Drive.Falhas.Enqueue(HttpStatusCode.Unauthorized);
        ambiente.Drive.Falhas.Enqueue(HttpStatusCode.TooManyRequests);
        await ambiente.Adapter.ObterArquivoAsync(GoogleSheetsFalso.IdPadrao, default);
        Assert.Equal(2, ambiente.Base.Google.Renovacoes);
        Assert.Single(ambiente.Esperas);
    }

    [Fact]
    public async Task PlanilhaNativaUsaSheetsESemDriveMantemCompatibilidade()
    {
        using var ambiente = new AmbienteXlsx();
        ambiente.Base.Google.AdicionarAba("Original");
        ambiente.Drive.Mime = "application/vnd.google-apps.spreadsheet";
        Assert.Equal("Original", Assert.Single((await ambiente.Provedor.ObterMetadadosAsync(GoogleSheetsFalso.IdPadrao, false)).Abas).Titulo);
        var credencial = ambiente.Base.Store.Carregar()!;
        ambiente.Base.Store.Salvar(credencial with { Escopos = GoogleOptions.Escopos });
        var antes = ambiente.Drive.Consultas;
        await ambiente.Provedor.ObterMetadadosAsync(GoogleSheetsFalso.IdPadrao, false);
        Assert.Equal(antes, ambiente.Drive.Consultas);
        ambiente.Base.Store.Salvar(credencial);
        ambiente.Drive.Mime = "application/pdf";
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoSuportada, (await Assert.ThrowsAsync<FalhaDePlanilhaException>(() =>
            ambiente.Provedor.ObterMetadadosAsync(GoogleSheetsFalso.IdPadrao, false))).Motivo);
    }

    [Theory]
    [InlineData("protegida")]
    [InlineData("compartilhada")]
    [InlineData("matricial")]
    [InlineData("tabela")]
    public async Task EstruturasNaoSuportadasSaoRecusadasSemUpload(string caso)
    {
        using var ambiente = new AmbienteXlsx();
        var partes = Partes(ambiente.Drive.Conteudo);
        var xml = Xml(partes["xl/worksheets/sheet1.xml"]);
        var intervalo = "Dados!B1";
        if (caso == "protegida") xml.Root!.Add(new XElement(Ns + "sheetProtection", new XAttribute("sheet", "1")));
        if (caso is "compartilhada" or "matricial")
        {
            xml.Descendants(Ns + "f").Single().SetAttributeValue("t", caso == "compartilhada" ? "shared" : "array");
            xml.Descendants(Ns + "f").Single().SetAttributeValue("ref", "C1:C2");
            intervalo = "Dados!C2";
        }
        if (caso == "tabela") xml.Root!.Add(new XElement(Ns + "tableParts"));
        partes["xl/worksheets/sheet1.xml"] = Encoding.UTF8.GetBytes(xml.ToString());
        ambiente.Drive.Conteudo = Pacote(partes);
        await ambiente.CadastrarAsync();
        var falha = await Assert.ThrowsAsync<FalhaDePlanilhaException>(() => caso == "tabela"
            ? ambiente.Servico.AdicionarLinhaAsync(new() { Planilha = "dados", Intervalo = "Dados!D1", Valores = [ValorDeCelula.DeNumero(2)] }, Origem)
            : ambiente.Servico.AtualizarAsync(Escrita(intervalo, 3), Origem));
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoSuportada, falha.Motivo);
        Assert.Empty(ambiente.Drive.Uploads);
    }

    [Fact]
    public void LimiteDeLeituraTiposLimpezaFormulaEErroDePacote()
    {
        var documento = new DocumentoXlsx(Fixture());
        var leitura = documento.Ler(IntervaloA1.DaAba("Dados"), 4, "1");
        Assert.True(leitura.Truncado);
        Assert.Equal("'Dados'!A1:D1", leitura.Intervalo);
        documento.Atualizar([new(IntervaloA1.Interpretar("Dados!A5:D5"),
            [[ValorDeCelula.DeTexto(" texto "), ValorDeCelula.DeBooleano(true), ValorDeCelula.DeTexto("=B1+1"), ValorDeCelula.DeNumero(9)]])]);
        documento.Atualizar([new(IntervaloA1.Interpretar("Dados!B1"), [[ValorDeCelula.Vazio]])]);
        var novo = new DocumentoXlsx(documento.Salvar());
        var cells = novo.Ler(IntervaloA1.Interpretar("Dados!A5:D5"), 4, "2").Celulas;
        Assert.Equal([" texto ", "TRUE", null, "9"], cells.Select(c => c.ValorExibido));
        Assert.Equal("=B1+1", cells[2].Formula);
        Assert.Empty(novo.Ler(IntervaloA1.Interpretar("Dados!B1"), 1, "2").Celulas);
        Assert.Equal(MotivoDaFalhaDePlanilha.Invalida, Assert.Throws<FalhaDePlanilhaException>(() => new DocumentoXlsx([1, 2, 3])).Motivo);
    }

    [Fact]
    public void SobrescreverFormulaRemoveSomenteOAlvoDaCadeiaDeCalculo()
    {
        var partes = Partes(Fixture());
        var relacoes = Xml(partes["xl/_rels/workbook.xml.rels"]);
        relacoes.Root!.Add(new XElement(relacoes.Root.Name.Namespace + "Relationship", new XAttribute("Id", "rId3"),
            new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/calcChain"),
            new XAttribute("Target", "calcChain.xml")));
        partes["xl/_rels/workbook.xml.rels"] = Encoding.UTF8.GetBytes(relacoes.ToString());
        partes["xl/calcChain.xml"] = Encoding.UTF8.GetBytes("""<calcChain xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><c r="C1" i="1"/><c r="C2"/></calcChain>""");
        var tipos = Xml(partes["[Content_Types].xml"]);
        tipos.Root!.Add(new XElement(tipos.Root.Name.Namespace + "Override", new XAttribute("PartName", "/xl/calcChain.xml"),
            new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.calcChain+xml")));
        partes["[Content_Types].xml"] = Encoding.UTF8.GetBytes(tipos.ToString());
        var documento = new DocumentoXlsx(Pacote(partes));
        documento.Atualizar([new(IntervaloA1.Interpretar("Dados!C1"), [[ValorDeCelula.DeNumero(7)]])]);
        var salvo = Partes(documento.Salvar());
        var cell = Assert.Single(Xml(salvo["xl/calcChain.xml"]).Root!.Elements());
        Assert.Equal(("C2", "1"), ((string?)cell.Attribute("r"), (string?)cell.Attribute("i")));
        Assert.Equal("7", new DocumentoXlsx(Pacote(salvo)).Ler(IntervaloA1.Interpretar("Dados!C1"), 1, "2").Celulas.Single().ValorExibido);
        documento.Atualizar([new(IntervaloA1.Interpretar("Dados!C2"), [[ValorDeCelula.DeNumero(9)]])]);
        salvo = Partes(documento.Salvar());
        Assert.False(salvo.ContainsKey("xl/calcChain.xml"));
        Assert.DoesNotContain(Xml(salvo["xl/_rels/workbook.xml.rels"]).Root!.Elements(), e =>
            ((string?)e.Attribute("Type"))?.EndsWith("/calcChain") == true);
        Assert.DoesNotContain(Xml(salvo["[Content_Types].xml"]).Root!.Elements(), e =>
            (string?)e.Attribute("PartName") == "/xl/calcChain.xml");
    }

    [Fact]
    public void WorkbookSemCalcPrRecebePedidoDeRecalculoERegiaoEsparsaNaoExplodeABusca()
    {
        var partes = Partes(Fixture());
        var workbook = Xml(partes["xl/workbook.xml"]);
        workbook.Root!.Element(Ns + "calcPr")!.Remove();
        partes["xl/workbook.xml"] = Encoding.UTF8.GetBytes(workbook.ToString());
        var documento = new DocumentoXlsx(Pacote(partes));
        documento.Atualizar([new(IntervaloA1.Interpretar("Dados!XFD1048576"), [[ValorDeCelula.DeNumero(1)]])]);
        var salvo = Partes(documento.Salvar());
        Assert.Equal("1", (string?)Xml(salvo["xl/workbook.xml"]).Root!.Element(Ns + "calcPr")!.Attribute("fullCalcOnLoad"));
        var novo = new DocumentoXlsx(Pacote(salvo));
        Assert.Equal(MotivoDaFalhaDePlanilha.NaoSuportada,
            Assert.Throws<FalhaDePlanilhaException>(() => novo.LerValores([IntervaloA1.DaAba("Dados")])).Motivo);
        Assert.Single(novo.LerValores([IntervaloA1.Interpretar("Dados!XFD1048576")]).Single().Linhas);
    }

    private static EscritaNaPlanilhaDto Escrita(string intervalo, double numero) => new()
    {
        Planilha = "dados", Alteracoes = [new() { Intervalo = intervalo, Valores = [[ValorDeCelula.DeNumero(numero)]] }]
    };

    private static byte[] Fixture() => Pacote(new Dictionary<string, byte[]>
    {
        ["_rels/.rels"] = Encoding.UTF8.GetBytes("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>"""),
        ["xl/workbook.xml"] = Encoding.UTF8.GetBytes("""<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Dados" sheetId="1" r:id="rId1"/></sheets><calcPr calcId="1"/></workbook>"""),
        ["xl/_rels/workbook.xml.rels"] = Encoding.UTF8.GetBytes("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>"""),
        ["xl/sharedStrings.xml"] = Encoding.UTF8.GetBytes("""<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><r><t>Inter</t></r><r><t>net</t></r></si></sst>"""),
        ["xl/worksheets/sheet1.xml"] = Encoding.UTF8.GetBytes("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><dimension ref="A1:D4"/><cols><col min="2" max="2" width="20" customWidth="1"/></cols><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" s="1"><v>12.5</v></c><c r="C1"><f>B1*2</f><v>25</v></c><c r="D1" t="inlineStr"><is><t>outro</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>internet extra</t></is></c><c r="B2"><v>2</v></c></row><row r="3"><c r="A3" t="inlineStr"><is><t>título</t></is></c></row><row r="4"><c r="D4" t="inlineStr"><is><t>fora</t></is></c></row></sheetData><mergeCells count="1"><mergeCell ref="A3:B3"/></mergeCells></worksheet>"""),
        ["xl/styles.xml"] = Encoding.UTF8.GetBytes("""<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Arial"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf/><xf numFmtId="2" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs></styleSheet>"""),
        ["[Content_Types].xml"] = Encoding.UTF8.GetBytes("""<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>"""),
        ["docProps/custom.xml"] = Encoding.UTF8.GetBytes("<propriedade>preservada</propriedade>")
    });

    private static Dictionary<string, byte[]> Partes(byte[] arquivo)
    {
        using var stream = new MemoryStream(arquivo);
        using var zip = new ZipArchive(stream);
        return zip.Entries.ToDictionary(e => e.FullName, e => { using var origem = e.Open(); using var destino = new MemoryStream(); origem.CopyTo(destino); return destino.ToArray(); });
    }

    private static XDocument Xml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return XDocument.Load(stream);
    }

    private static byte[] Pacote(Dictionary<string, byte[]> partes)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var parte in partes) { using var destino = zip.CreateEntry(parte.Key).Open(); destino.Write(parte.Value); }
        return stream.ToArray();
    }

    private sealed class AmbienteXlsx : IDisposable
    {
        internal AmbienteDePlanilhas Base { get; } = new();
        internal DriveFalso Drive { get; }
        internal HttpClient Http { get; }
        internal GoogleOAuthService OAuth { get; }
        internal GoogleDriveXlsxAdapter Adapter { get; }
        internal GooglePlanilhasAdapter Provedor { get; }
        internal List<TimeSpan> Esperas { get; } = [];
        internal AuditoriaFalsa Auditoria { get; } = new();
        internal PlanilhasAppService Servico { get; }
        internal AmbienteXlsx()
        {
            Base.Store.Salvar(Base.Store.Carregar()! with { Escopos = GoogleOptions.Escopos + " " + GoogleOptions.EscopoDeDrive });
            Drive = new DriveFalso(Base.Google);
            Http = new HttpClient(Drive);
            OAuth = new GoogleOAuthService(Base.Opcoes, Base.Store, Http);
            Adapter = new GoogleDriveXlsxAdapter(OAuth, Http, (delay, _) => { Esperas.Add(delay); return Task.CompletedTask; });
            Provedor = new GooglePlanilhasAdapter(OAuth, new GoogleSheetsAdapter(OAuth, Http), Adapter);
            Servico = new PlanilhasAppService(Provedor, OAuth, Base.Cadastro, Auditoria);
        }
        internal Task CadastrarAsync() => Servico.CadastrarAsync("dados", GoogleSheetsFalso.IdPadrao);
        public void Dispose() { OAuth.Dispose(); Http.Dispose(); Base.Dispose(); }
    }

    private sealed class AuditoriaFalsa : IAuditoriaDePlanilhas
    {
        internal List<string> Linhas { get; } = [];
        public Task RegistrarAsync(RegistroDeAuditoriaDePlanilha registro, CancellationToken cancellationToken = default)
        { Linhas.Add(System.Text.Json.JsonSerializer.Serialize(registro)); return Task.CompletedTask; }
    }

    private sealed class DriveFalso(GoogleSheetsFalso sheets) : DelegatingHandler(sheets)
    {
        internal byte[] Conteudo { get; set; } = Fixture();
        internal int Versao { get; set; } = 1;
        internal int Consultas { get; private set; }
        internal string Mime { get; set; } = GoogleDriveXlsxAdapter.MimeXlsx;
        internal bool TemETag { get; set; } = true;
        internal bool ETagFraco { get; set; }
        internal bool PodeEditar { get; set; } = true;
        internal bool PodeBaixar { get; set; } = true;
        internal bool AlterarAoBaixar { get; set; }
        internal bool AlterarAoEnviar { get; set; }
        internal int AlterarNaConsulta { get; set; }
        internal HttpStatusCode StatusDoUpload { get; set; } = HttpStatusCode.OK;
        internal Queue<HttpStatusCode> Falhas { get; } = [];
        internal List<(string Url, string? Token)> Requisicoes { get; } = [];
        internal List<(string Url, string? IfMatch, string? Mime)> Uploads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requisicoes.Add((url, request.Headers.Authorization?.Parameter));
            if (request.RequestUri.Host != "www.googleapis.com") return await base.SendAsync(request, cancellationToken);
            if (Falhas.TryDequeue(out var erro)) return new HttpResponseMessage(erro);
            if (request.Method == HttpMethod.Put)
            {
                Uploads.Add((url, request.Headers.IfMatch.FirstOrDefault()?.ToString(), request.Content?.Headers.ContentType?.MediaType));
                if (AlterarAoEnviar) Versao++;
                if (request.Headers.IfMatch.FirstOrDefault()?.ToString() != $"\"v{Versao}\"") return new(HttpStatusCode.PreconditionFailed);
                if (StatusDoUpload != HttpStatusCode.OK) return new(StatusDoUpload);
                Conteudo = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                Versao++;
                return Json(new JsonObject { ["id"] = GoogleSheetsFalso.IdPadrao, ["version"] = Versao.ToString() });
            }
            if (url.Contains("alt=media"))
            {
                var resposta = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Conteudo) };
                if (AlterarAoBaixar) Versao++;
                return resposta;
            }
            Consultas++;
            if (Consultas == AlterarNaConsulta) Versao++;
            var metadados = Json(new JsonObject
            {
                ["id"] = GoogleSheetsFalso.IdPadrao, ["title"] = "Dados.xlsx", ["mimeType"] = Mime,
                ["version"] = Versao.ToString(), ["fileSize"] = Conteudo.Length.ToString(),
                ["etag"] = TemETag ? (ETagFraco ? "W/" : "") + $"\"v{Versao}\"" : null,
                ["capabilities"] = new JsonObject { ["canEdit"] = PodeEditar, ["canDownload"] = PodeBaixar }
            });
            if (TemETag) metadados.Headers.ETag = new EntityTagHeaderValue($"\"v{Versao}\"", ETagFraco);
            return metadados;
        }
        private static HttpResponseMessage Json(JsonObject json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
