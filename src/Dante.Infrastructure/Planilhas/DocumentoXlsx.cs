using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Dante.Application.Planilhas;

namespace Dante.Infrastructure.Planilhas;

// Editor limitado de valores OOXML: copia todas as partes e só reescreve as abas alteradas e calcPr.
// Sem conversão de formato ou motor de cálculo. Fórmulas usam o último resultado salvo pelo editor.
internal sealed class DocumentoXlsx
{
    internal const int MaximoDeBytes = 25 * 1024 * 1024;
    private const int MaximoExpandido = 100 * 1024 * 1024;
    private const int MaximoDeCelulas = 200_000;
    private const int MaximoDeGradeParaBusca = 250_000;
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private readonly Dictionary<string, byte[]> partes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XDocument> alteradas = new(StringComparer.Ordinal);
    private readonly List<(string Nome, string Caminho, long Id, XDocument Xml)> abas = [];
    private readonly string[] textos;
    private readonly XDocument workbook;
    private readonly string caminhoWorkbook;
    private readonly string caminhoRelacoesWorkbook;
    private readonly string? caminhoCalcChain;
    internal const string Observacao = "Arquivo XLSX no Drive. Valores numéricos são brutos; fórmulas mostram o último " +
        "resultado salvo, que pode estar desatualizado. Não há cálculo local nem reprodução da formatação de exibição. " +
        "Para escrever números, use valores JSON numéricos; texto é preservado literalmente (exceto =fórmula).";

    internal DocumentoXlsx(byte[] conteudo)
    {
        try
        {
            if (conteudo.Length > MaximoDeBytes) throw Limite();
            using var stream = new MemoryStream(conteudo, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count > 10_000 || zip.Entries.Sum(e => e.Length) > MaximoExpandido) throw Limite();
            long expandido = 0;
            foreach (var entry in zip.Entries)
            {
                using var origem = entry.Open();
                using var bytes = new MemoryStream();
                var buffer = new byte[64 * 1024];
                int lidos;
                while ((lidos = origem.Read(buffer)) > 0)
                {
                    expandido += lidos;
                    if (expandido > MaximoExpandido) throw Limite();
                    bytes.Write(buffer, 0, lidos);
                }
                if (!partes.TryAdd(entry.FullName, bytes.ToArray())) throw Invalido();
            }
            var relacoesRaiz = Xml("_rels/.rels");
            var documento = relacoesRaiz.Root!.Elements().SingleOrDefault(e =>
                ((string?)e.Attribute("Type"))?.EndsWith("/officeDocument", StringComparison.Ordinal) == true);
            if (documento is null || (string?)documento.Attribute("TargetMode") == "External") throw Invalido();
            caminhoWorkbook = ResolverCaminho("", (string?)documento.Attribute("Target") ?? "");
            workbook = Xml(caminhoWorkbook);
            if (workbook.Root?.Name != Ns + "workbook") throw NaoSuportado("Esta variante de XLSX não é suportada.");
            var posicao = caminhoWorkbook.LastIndexOf('/');
            var pasta = posicao < 0 ? "" : caminhoWorkbook[..(posicao + 1)];
            caminhoRelacoesWorkbook = pasta + "_rels/" + caminhoWorkbook[(posicao + 1)..] + ".rels";
            var listaRelacoes = Xml(caminhoRelacoesWorkbook).Root!.Elements()
                .Where(e => (string?)e.Attribute("TargetMode") != "External")
                .ToArray();
            var relacoes = listaRelacoes.ToDictionary(e => (string)e.Attribute("Id")!,
                e => ResolverCaminho(pasta, (string)e.Attribute("Target")!));
            foreach (var aba in workbook.Root.Element(Ns + "sheets")!.Elements(Ns + "sheet"))
            {
                var caminho = relacoes[(string)aba.Attribute(Rel + "id")!];
                var xml = Xml(caminho);
                if (xml.Root?.Name != Ns + "worksheet") throw NaoSuportado("XLSX com abas que não são grades não é suportado.");
                if (xml.Descendants(Ns + "c").Take(MaximoDeCelulas + 1).Count() > MaximoDeCelulas) throw Limite();
                abas.Add(((string)aba.Attribute("name")!, caminho, (long)aba.Attribute("sheetId")!, xml));
            }
            var caminhoTextos = listaRelacoes.FirstOrDefault(e =>
                ((string?)e.Attribute("Type"))?.EndsWith("/sharedStrings", StringComparison.Ordinal) == true) is { } compartilhados
                ? relacoes[(string)compartilhados.Attribute("Id")!] : null;
            textos = caminhoTextos is null ? [] : Xml(caminhoTextos).Root!.Elements(Ns + "si")
                .Select(TextoRico).ToArray();
            caminhoCalcChain = listaRelacoes.FirstOrDefault(e =>
                ((string?)e.Attribute("Type"))?.EndsWith("/calcChain", StringComparison.Ordinal) == true) is { } cadeia
                ? relacoes[(string)cadeia.Attribute("Id")!] : null;
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or InvalidOperationException or
                                     ArgumentException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw Invalido();
        }
    }

    internal PlanilhaDto Metadados(string id, string titulo, bool calcularAreaUsada) => new()
    {
        IdDaPlanilha = id, Titulo = titulo, Url = $"https://drive.google.com/file/d/{Uri.EscapeDataString(id)}/view",
        Observacao = Observacao,
        Abas = abas.Select((aba, indice) => new AbaDaPlanilhaDto
        {
            IdDaAba = aba.Id, Titulo = aba.Nome, Indice = indice, Linhas = 1_048_576, Colunas = 16_384,
            AreaUsada = calcularAreaUsada ? AreaUsada(aba.Xml)?.Celulas : null,
            Mesclagens = Mesclagens(aba.Xml).Select(m => m.Celulas).ToArray()
        }).ToArray()
    };

    internal IntervaloDaPlanilhaDto Ler(IntervaloA1 pedido, int limite, string revisao)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limite, 1);
        var aba = Aba(pedido);
        var area = pedido.AbaInteira ? AreaUsada(aba.Xml)?.NaAba(aba.Nome) : pedido;
        if (area is null) return new() { Aba = aba.Nome, Intervalo = pedido.ToString(), Revisao = revisao, Observacao = Observacao };
        var truncado = area.QuantidadeDeCelulas > limite;
        if (truncado)
        {
            var colunas = Math.Min(area.QuantidadeDeColunas, limite);
            var linhas = Math.Max(1, limite / colunas);
            area = IntervaloA1.Retangulo(aba.Nome, area.Linha, area.Coluna, area.Linha + linhas - 1, area.Coluna + colunas - 1);
        }
        var mesclagens = Mesclagens(aba.Xml).Where(m => m.Intersecta(area)).ToArray();
        return new()
        {
            Aba = aba.Nome, Intervalo = area.NaAba(aba.Nome).ToString(), Revisao = revisao, Observacao = Observacao,
            Truncado = truncado, Mesclagens = mesclagens.Select(m => m.Celulas).ToArray(),
            Celulas = Celulas(aba.Xml).Where(c => area.Contem(c.Linha, c.Coluna))
                .Select(c => c with { Mesclagem = mesclagens.FirstOrDefault(m => (m.Linha, m.Coluna) == (c.Linha, c.Coluna))?.Celulas })
                .Where(c => !c.EstaVazia || c.Mesclagem is not null).OrderBy(c => c.Linha).ThenBy(c => c.Coluna).ToArray()
        };
    }

    internal IReadOnlyList<ValoresLidos> LerValores(IReadOnlyList<IntervaloA1> pedidos)
    {
        var resultado = new List<ValoresLidos>();
        long total = 0;
        foreach (var pedido in pedidos)
        {
            var aba = Aba(pedido);
            var inicio = IntervaloA1.DaCelula(aba.Nome, pedido.Linha, pedido.Coluna);
            var ocupadas = Celulas(aba.Xml).Where(c => pedido.Contem(c.Linha, c.Coluna) && !c.EstaVazia).ToArray();
            if (ocupadas.Length == 0) { resultado.Add(new(inicio, [], Observacao)); continue; }
            var ultimaLinha = ocupadas.Max(c => c.Linha);
            var ultimaColuna = ocupadas.Max(c => c.Coluna);
            total += (long)(ultimaLinha - inicio.Linha + 1) * (ultimaColuna - inicio.Coluna + 1);
            if (total > MaximoDeGradeParaBusca) throw NaoSuportado("A região XLSX é grande demais para buscar; restrinja o intervalo.");
            var valores = ocupadas.ToDictionary(c => (c.Linha, c.Coluna), c => c.ValorExibido ?? "");
            var linhas = new List<IReadOnlyList<string>>();
            for (var l = inicio.Linha; l <= ultimaLinha; l++)
            {
                var linha = new string[ultimaColuna - inicio.Coluna + 1];
                for (var c = inicio.Coluna; c <= ultimaColuna; c++) linha[c - inicio.Coluna] = valores.GetValueOrDefault((l, c), "");
                linhas.Add(linha);
            }
            resultado.Add(new(inicio, linhas, Observacao));
        }
        return resultado;
    }

    internal void Atualizar(IReadOnlyList<ValoresParaEscrita> escritas)
    {
        if (partes.Keys.Any(p => p.StartsWith("_xmlsignatures/", StringComparison.Ordinal)))
            throw NaoSuportado("XLSX assinado digitalmente não pode ser alterado por esta ferramenta.");
        var alvos = new HashSet<(long Aba, string Endereco)>();
        foreach (var escrita in escritas)
        {
            var aba = Aba(escrita.Intervalo);
            if (aba.Xml.Root!.Element(Ns + "sheetProtection") is not null)
                throw NaoSuportado("A aba XLSX está protegida; remova a proteção no editor antes de alterar.");
            var formulasEmGrupo = aba.Xml.Descendants(Ns + "f").Where(f =>
                (string?)f.Attribute("t") is "shared" or "array" or "dataTable").ToArray();
            for (var i = 0; i < escrita.Valores.Count; i++)
            for (var j = 0; j < escrita.Valores[i].Count; j++)
            {
                var linha = escrita.Intervalo.Linha + i;
                var coluna = escrita.Intervalo.Coluna + j;
                if (linha > 1_048_576 || coluna > 16_384) throw NaoSuportado("O alvo excede a grade do XLSX.");
                var endereco = IntervaloA1.Endereco(linha, coluna);
                if (formulasEmGrupo.Any(f => (string?)f.Parent?.Attribute("r") == endereco ||
                    f.Attribute("ref") is { } referencia && IntervaloA1.Interpretar(referencia.Value).Contem(linha, coluna)))
                    throw NaoSuportado("O alvo contém fórmula compartilhada ou matricial; altere-a no editor.");
                var mesclagem = Mesclagens(aba.Xml).FirstOrDefault(m => m.Contem(linha, coluna));
                if (mesclagem is not null && (mesclagem.Linha, mesclagem.Coluna) != (linha, coluna))
                    throw NaoSuportado("O alvo fica dentro de uma mesclagem; escreva na célula inicial.");
                Escrever(aba.Xml, linha, coluna, escrita.Valores[i][j]);
                alvos.Add((aba.Id, endereco));
            }
            alteradas[aba.Caminho] = aba.Xml;
        }
        // O editor recalcula ao abrir. Fórmulas fora do alvo e seus caches são preservados.
        var calc = workbook.Root!.Element(Ns + "calcPr");
        if (calc is null)
        {
            calc = new XElement(Ns + "calcPr");
            string[] posteriores = ["oleSize", "customWorkbookViews", "pivotCaches", "smartTagPr", "smartTagTypes",
                "webPublishing", "fileRecoveryPr", "webPublishObjects", "extLst"];
            var proximo = workbook.Root.Elements().FirstOrDefault(e => posteriores.Contains(e.Name.LocalName));
            if (proximo is null) workbook.Root.Add(calc); else proximo.AddBeforeSelf(calc);
        }
        calc.SetAttributeValue("fullCalcOnLoad", "1");
        calc.SetAttributeValue("forceFullCalc", "1");
        alteradas[caminhoWorkbook] = workbook;
        if (caminhoCalcChain is not null && partes.ContainsKey(caminhoCalcChain))
        {
            var cadeia = alteradas.GetValueOrDefault(caminhoCalcChain) ?? Xml(caminhoCalcChain);
            long? idAba = null;
            foreach (var cell in cadeia.Root!.Elements(Ns + "c").ToArray())
            {
                idAba = (long?)cell.Attribute("i") ?? idAba;
                if (idAba is null) throw NaoSuportado("A cadeia de cálculo do XLSX não identifica a aba; altere no editor.");
                // i é herdado da entrada anterior. Explicitá-lo conserva o contexto ao remover uma entrada alvo.
                cell.SetAttributeValue("i", idAba);
                if (alvos.Contains((idAba.Value, (string?)cell.Attribute("r") ?? ""))) cell.Remove();
            }
            alteradas[caminhoCalcChain] = cadeia;
            if (!cadeia.Root.Elements(Ns + "c").Any())
            {
                partes.Remove(caminhoCalcChain);
                alteradas.Remove(caminhoCalcChain);
                var relacoes = Xml(caminhoRelacoesWorkbook);
                relacoes.Root!.Elements().Where(e =>
                    ((string?)e.Attribute("Type"))?.EndsWith("/calcChain", StringComparison.Ordinal) == true).Remove();
                alteradas[caminhoRelacoesWorkbook] = relacoes;
                var tipos = Xml("[Content_Types].xml");
                var declaracao = tipos.Root!.Elements().FirstOrDefault(e =>
                    (string?)e.Attribute("PartName") == "/" + caminhoCalcChain);
                if (declaracao is not null)
                {
                    declaracao.Remove();
                    alteradas["[Content_Types].xml"] = tipos;
                }
            }
        }
    }

    internal IntervaloA1 AdicionarLinha(IntervaloA1 tabela, IReadOnlyList<ValorDeCelula> valores)
    {
        var aba = Aba(tabela);
        var ocupadas = Celulas(aba.Xml).Where(c => c.Coluna >= tabela.Coluna && c.Coluna <= tabela.ColunaFinal && !c.EstaVazia)
            .Select(c => c.Linha).ToHashSet();
        var linha = tabela.Linha;
        while (ocupadas.Contains(linha)) linha++;
        var alvo = IntervaloA1.Retangulo(aba.Nome, linha, tabela.Coluna, linha, tabela.Coluna + valores.Count - 1);
        // Não estender tabelas estruturadas nem escrever sobre conteúdo nas colunas extras.
        if (aba.Xml.Root!.Element(Ns + "tableParts") is not null)
            throw NaoSuportado("Adicionar linha em aba XLSX com tabela estruturada não é suportado; use células exatas.");
        if (Celulas(aba.Xml).Any(c => alvo.Contem(c.Linha, c.Coluna) && !c.EstaVazia))
            throw NaoSuportado("A nova linha contém conteúdo; selecione outro intervalo.");
        Atualizar([new(alvo, [valores])]);
        return alvo;
    }

    internal byte[] Salvar()
    {
        using var destino = new MemoryStream();
        using (var zip = new ZipArchive(destino, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var parte in partes)
            {
                using var stream = zip.CreateEntry(parte.Key, CompressionLevel.Optimal).Open();
                if (alteradas.TryGetValue(parte.Key, out var xml)) xml.Save(stream, SaveOptions.DisableFormatting);
                else stream.Write(parte.Value);
            }
        if (destino.Length > MaximoDeBytes) throw Limite();
        return destino.ToArray();
    }

    private IEnumerable<CelulaDaPlanilhaDto> Celulas(XDocument xml) => xml.Descendants(Ns + "c").Select(c =>
    {
        var endereco = (string?)c.Attribute("r") ?? throw Invalido();
        var coordenada = Coordenadas(endereco);
        var tipo = (string?)c.Attribute("t");
        var valor = c.Element(Ns + "v")?.Value ?? "";
        ValorDeCelula? bruto = tipo switch
        {
            "s" => int.TryParse(valor, out var indice) && indice >= 0 && indice < textos.Length
                ? ValorDeCelula.DeTexto(textos[indice]) : throw Invalido(),
            "inlineStr" => ValorDeCelula.DeTexto(TextoRico(c.Element(Ns + "is"))),
            "str" or "d" => ValorDeCelula.DeTexto(valor),
            "b" => ValorDeCelula.DeBooleano(valor == "1"),
            "e" => ValorDeCelula.DeErro(valor),
            _ => double.TryParse(valor, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
                ? ValorDeCelula.DeNumero(n) : null
        };
        var formula = c.Element(Ns + "f");
        return new CelulaDaPlanilhaDto
        {
            Endereco = endereco, Linha = coordenada.Linha, Coluna = coordenada.Coluna, ValorBruto = bruto,
            ValorExibido = bruto?.ToString(), Formula = formula is null ? null :
                formula.Value.Length == 0 ? "[fórmula compartilhada; veja a célula inicial]" : "=" + formula.Value
        };
    });

    private IntervaloA1? AreaUsada(XDocument xml)
    {
        var celulas = Celulas(xml).Where(c => !c.EstaVazia).ToArray();
        return celulas.Length == 0 ? null : IntervaloA1.Retangulo(null, 1, 1, celulas.Max(c => c.Linha), celulas.Max(c => c.Coluna));
    }

    private static IEnumerable<IntervaloA1> Mesclagens(XDocument xml) => xml.Descendants(Ns + "mergeCell")
        .Select(m => Coordenadas((string)m.Attribute("ref")!, celulaUnica: false));

    private static IntervaloA1 Coordenadas(string endereco, bool celulaUnica = true) =>
        IntervaloA1.TentarInterpretar(endereco, out var coordenada) && !coordenada!.AbaInteira && coordenada.Aba is null &&
        (!celulaUnica || coordenada.CelulaUnica) && coordenada.LinhaFinal <= 1_048_576 && coordenada.ColunaFinal <= 16_384
            ? coordenada : throw Invalido();

    private (string Nome, string Caminho, long Id, XDocument Xml) Aba(IntervaloA1 intervalo) =>
        abas.FirstOrDefault(a => a.Nome == intervalo.Aba) is var aba && aba.Xml is not null ? aba :
            throw new FalhaDePlanilhaException(MotivoDaFalhaDePlanilha.Invalida, "A aba informada não existe no XLSX.");

    private static void Escrever(XDocument xml, int linha, int coluna, ValorDeCelula valor)
    {
        var dados = xml.Root!.Element(Ns + "sheetData") ?? throw Invalido();
        var row = dados.Elements(Ns + "row").FirstOrDefault(r => (int?)r.Attribute("r") == linha);
        if (row is null)
        {
            row = new XElement(Ns + "row", new XAttribute("r", linha));
            var proxima = dados.Elements(Ns + "row").FirstOrDefault(r => (int?)r.Attribute("r") > linha);
            if (proxima is null) dados.Add(row); else proxima.AddBeforeSelf(row);
        }
        var endereco = IntervaloA1.Endereco(linha, coluna);
        var cell = row.Elements(Ns + "c").FirstOrDefault(c => (string?)c.Attribute("r") == endereco);
        if (cell is null)
        {
            cell = new XElement(Ns + "c", new XAttribute("r", endereco));
            var proxima = row.Elements(Ns + "c").FirstOrDefault(c =>
                Coordenadas((string)c.Attribute("r")!).Coluna > coluna);
            if (proxima is null) row.Add(cell); else proxima.AddBeforeSelf(cell);
        }
        // Conserva s (estilo), atributos e extensões; troca apenas tipo, fórmula e valor.
        cell.Elements().Where(e => e.Name == Ns + "f" || e.Name == Ns + "v" || e.Name == Ns + "is").Remove();
        cell.SetAttributeValue("t", null);
        if (valor.EstaVazio) { }
        else if (valor.Tipo == TipoDeValorDaCelula.Numero)
            cell.AddFirst(new XElement(Ns + "v", valor.Numero!.Value.ToString("R", CultureInfo.InvariantCulture)));
        else if (valor.Tipo == TipoDeValorDaCelula.Booleano)
        {
            cell.SetAttributeValue("t", "b");
            cell.AddFirst(new XElement(Ns + "v", valor.Booleano!.Value ? "1" : "0"));
        }
        else if (valor.Texto?.StartsWith('=') == true)
            cell.AddFirst(new XElement(Ns + "f", valor.Texto[1..]));
        else
        {
            cell.SetAttributeValue("t", "inlineStr");
            cell.AddFirst(new XElement(Ns + "is", new XElement(Ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"),
                valor.Texto ?? "")));
        }
        var dimension = xml.Root.Element(Ns + "dimension");
        if (dimension?.Attribute("ref") is { } referencia)
        {
            var anterior = IntervaloA1.Interpretar(referencia.Value);
            dimension.SetAttributeValue("ref", IntervaloA1.Retangulo(null, Math.Min(anterior.Linha, linha),
                Math.Min(anterior.Coluna, coluna), Math.Max(anterior.LinhaFinal, linha), Math.Max(anterior.ColunaFinal, coluna)).Celulas);
        }
    }

    private XDocument Xml(string caminho)
    {
        if (!partes.TryGetValue(caminho, out var bytes)) throw Invalido();
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximoExpandido
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static string TextoRico(XElement? elemento) => string.Concat(elemento?.Descendants(Ns + "t")
        .Where(t => !t.Ancestors(Ns + "rPh").Any()).Select(t => t.Value) ?? []);

    private static string ResolverCaminho(string pasta, string alvo)
    {
        var uri = new Uri(new Uri("https://xlsx.invalid/" + pasta), alvo);
        if (uri.Host != "xlsx.invalid") throw Invalido();
        return Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
    }

    private static FalhaDePlanilhaException Invalido() => new(MotivoDaFalhaDePlanilha.Invalida, "O arquivo não é um XLSX válido ou contém estrutura não suportada.");
    private static FalhaDePlanilhaException Limite() => NaoSuportado("XLSX excede os limites: 25 MB, 100 MB descompactado ou 200 mil células por aba.");
    private static FalhaDePlanilhaException NaoSuportado(string mensagem) => new(MotivoDaFalhaDePlanilha.NaoSuportada, mensagem);
}
