using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dante.Worker.Planilhas;
using static Dante.Tests.ServidorMcpDePlanilhasTests;

namespace Dante.Tests;

// #224: a planilha de treino como cenário E2E da capacidade genérica — blocos por dia com título mesclado, cabeçalhos
// repetidos, o mesmo item em dois blocos, fórmula e uma aba de histórico. Tudo passa pelas ferramentas MCP, como um
// agente faria; nenhum código de produção conhece treino (guard abaixo). A planilha real do usuário é exercitada pelo
// LivePlanilhasEvidenceTests, opt-in.
public sealed class PlanilhaDeTreinoCenarioTests
{
    private static AmbienteDePlanilhas Treino()
    {
        var ambiente = new AmbienteDePlanilhas();
        var google = ambiente.Google;
        google.Titulo = "Planilha de treino";
        google.AdicionarAba("Semana 1");
        google.AdicionarAba("Histórico");
        void Linha(string aba, int linha, params object?[] valores)
        {
            for (var i = 0; i < valores.Length; i++)
                if (valores[i] is { } valor) google.Definir(aba, $"{(char)('A' + i)}{linha}", valor);
        }
        Linha("Semana 1", 1, "Treino A — Peito");
        google.Mesclar("Semana 1", "A1:F1");
        Linha("Semana 1", 2, "Exercício", "Séries", "Reps", "Carga", "Top", "Obs");
        Linha("Semana 1", 3, "Supino inclinado", 4, "8-10", 25, null, "");
        Linha("Semana 1", 4, "Crucifixo", 3, "12", 12);
        Linha("Semana 1", 5, "Tríceps corda", 3, "12", 20);
        google.Definir("Semana 1", "E5", 22, formula: "=D5*1,1", exibido: "22");
        Linha("Semana 1", 7, "Treino B — Costas");
        google.Mesclar("Semana 1", "A7:F7");
        Linha("Semana 1", 8, "Exercício", "Séries", "Reps", "Carga", "Top", "Obs");
        Linha("Semana 1", 9, "Remada curvada", 4, "8", 40);
        Linha("Semana 1", 10, "Supino inclinado", 3, "10", 22);
        Linha("Histórico", 1, "Data", "Exercício", "Carga");
        Linha("Histórico", 2, "22/09/2026", "Supino inclinado", 22);
        Linha("Histórico", 3, "29/09/2026", "Remada curvada", 40);
        Linha("Histórico", 4, "01/10/2026", "Supino inclinado", 25);
        return ambiente;
    }

    [Fact]
    public async Task AgenteConsultaLocalizaEAtualizaUmBlocoSemAdivinharEPreservaORestante()
    {
        using var ambiente = Treino();
        var servidor = Servidor(ambiente);
        var url = $"https://docs.google.com/spreadsheets/d/{ambiente.Google.IdDaPlanilha}/edit";
        var (texto, erro) = await Chamar(servidor, "cadastrar_planilha", new JsonObject { ["alias"] = "treino", ["url_ou_id"] = url });
        Assert.False(erro, texto);

        (texto, _) = await Chamar(servidor, "descrever_planilha", new JsonObject { ["planilha"] = "treino" });
        Assert.Contains("'Semana 1' (grade 1000x26, área usada A1:F10) mesclagens: A1:F1, A7:F7", texto);
        Assert.Contains("'Histórico' (grade 1000x26, área usada A1:C4)", texto);

        // "qual foi a última carga registrada para supino inclinado?" — o agente busca e lê as linhas com coordenadas.
        (texto, _) = await Chamar(servidor, "buscar_na_planilha", new JsonObject { ["planilha"] = "treino", ["texto"] = "supino inclinado" });
        Assert.StartsWith("4 ocorrência(s)", texto);
        Assert.Contains("'Semana 1'!A3 = \"Supino inclinado\" | linha 3: A3=\"Supino inclinado\"; B3=\"4\"; C3=\"8-10\"; D3=\"25\"", texto);
        Assert.Contains("'Semana 1'!A10", texto);
        Assert.Contains("'Histórico'!B4 = \"Supino inclinado\" | linha 4: A4=\"01/10/2026\"; B4=\"Supino inclinado\"; C4=\"25\"", texto);

        var antes = Instantaneo(ambiente);
        // "registre 32,5 na coluna Top do exercício correspondente": o mesmo item em dois blocos é ambíguo.
        (texto, erro) = await Chamar(servidor, "atualizar_por_referencia", new JsonObject
        {
            ["planilha"] = "treino", ["referencia"] = "Supino inclinado", ["aba"] = "Semana 1", ["coluna"] = "E", ["valor"] = "32,5"
        });
        Assert.True(erro);
        Assert.Contains("corresponde a 2 células. Nada foi alterado", texto);
        Assert.Contains("- 'Semana 1'!A3", texto);
        Assert.Contains("- 'Semana 1'!A10", texto);
        Assert.Empty(ambiente.Google.Escritas);

        // Com o bloco resolvido (o usuário confirmou o treino A), a escrita é determinística.
        (texto, _) = await Chamar(servidor, "ler_intervalo", new JsonObject { ["planilha"] = "treino", ["intervalo"] = "'Semana 1'!A1:F5" });
        Assert.Contains("A1 = \"Treino A — Peito\" [mesclagem A1:F1]", texto);
        Assert.Contains("E5 = \"22\" (fórmula =D5*1,1)", texto);
        (texto, erro) = await Chamar(servidor, "atualizar_por_referencia", new JsonObject
        {
            ["planilha"] = "treino", ["referencia"] = "Supino inclinado", ["intervalo"] = "'Semana 1'!A1:F5", ["coluna"] = "E",
            ["valor"] = "32,5", ["valor_esperado"] = ""
        });
        Assert.False(erro, texto);
        Assert.Contains("'Semana 1'!E3: (vazio) → \"32,5\"", texto);
        Assert.Equal(32.5, ambiente.Google.Obter("Semana 1", "E3")!.Numero);
        var depois = Instantaneo(ambiente);
        Assert.Equal(antes.Where(c => c.Key != "Semana 1!E3"), depois.Where(c => c.Key != "Semana 1!E3"));

        // Fórmula e célula interna de mesclagem não são sobrescritas sem decisão explícita.
        (texto, erro) = await Chamar(servidor, "atualizar_celulas", new JsonObject
        {
            ["planilha"] = "treino",
            ["alteracoes"] = new JsonArray(new JsonObject { ["intervalo"] = "'Semana 1'!E5", ["valores"] = new JsonArray(new JsonArray(30)) })
        });
        Assert.True(erro);
        Assert.Contains("fórmula", texto);
        (texto, erro) = await Chamar(servidor, "atualizar_celulas", new JsonObject
        {
            ["planilha"] = "treino",
            ["alteracoes"] = new JsonArray(new JsonObject { ["intervalo"] = "'Semana 1'!C7", ["valores"] = new JsonArray(new JsonArray("x")) })
        });
        Assert.True(erro);
        Assert.Contains("mesclagem A7:F7", texto);

        (texto, erro) = await Chamar(servidor, "adicionar_linha", new JsonObject
        {
            ["planilha"] = "treino", ["intervalo_da_tabela"] = "Histórico!A1:C1",
            ["valores"] = new JsonArray("06/10/2026", "Supino inclinado", 32.5)
        });
        Assert.False(erro, texto);
        Assert.Equal(("06/10/2026", "32,5"), (ambiente.Google.Exibido("Histórico", "A5"), ambiente.Google.Exibido("Histórico", "C5")));

        var auditoria = ambiente.LinhasDeAuditoria().Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal(4, auditoria.Length);
        Assert.All(auditoria, linha => Assert.Equal(("mcp:telegram:42", "claude", "treino"),
            (linha.GetProperty("Origem").GetString(), linha.GetProperty("Agente").GetString(), linha.GetProperty("Alias").GetString())));
        Assert.Equal(("Semana 1", "E3", "atualizar_por_referencia"), (auditoria[0].GetProperty("Aba").GetString(),
            auditoria[0].GetProperty("Endereco").GetString(), auditoria[0].GetProperty("Operacao").GetString()));
    }

    // AD-55: o núcleo e o adapter não conhecem o domínio dos dados; a planilha de treino é só um cenário.
    [Fact]
    public void IntegracaoNaoConheceConceitosDeDominio()
    {
        var raiz = Raiz();
        string[] caminhos =
        [
            "src/Dante.Application/Planilhas", "src/Dante.Infrastructure/Google", "src/Dante.Infrastructure/Planilhas",
            "src/Dante.Infrastructure/Composicao/PlanilhasServiceCollectionExtensions.cs", "src/Dante.Worker/Planilhas",
            "src/Dante.Worker/Telegram/TelegramPlanilhas.cs", "src/Dante.Worker/Telegram/TelegramPollingService.Planilhas.cs"
        ];
        var arquivos = caminhos.Select(c => Path.Combine(raiz, c)).SelectMany(c => File.Exists(c) ? [c] :
            Directory.EnumerateFiles(c, "*.cs", SearchOption.AllDirectories)).ToArray();
        Assert.True(arquivos.Length > 20);
        var dominio = new Regex(@"\b(treino|exerc[ií]cio|carga|s[eé]ries?|repeti[cç][aã]o|repeti[cç][oõ]es|supino|top ?set)\b",
            RegexOptions.IgnoreCase);
        Assert.Empty(arquivos.Where(a => dominio.IsMatch(File.ReadAllText(a))).Select(Path.GetFileName));

        var application = typeof(Dante.Application.Planilhas.PlanilhasAppService).Assembly;
        Assert.DoesNotContain(application.GetTypes(), t => t.Name.Contains("Google", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(raiz, "src/Dante.Application/Planilhas"), "*.cs", SearchOption.AllDirectories)
            .Where(a => Regex.IsMatch(File.ReadAllText(a), "google|oauth|googleapis", RegexOptions.IgnoreCase)).Select(Path.GetFileName));
    }

    private static Dictionary<string, (string?, string?)> Instantaneo(AmbienteDePlanilhas ambiente) => ambiente.Google.Abas
        .SelectMany(aba => aba.Celulas.Select(c => KeyValuePair.Create(
            $"{aba.Titulo}!{Dante.Application.Planilhas.IntervaloA1.Endereco(c.Key.Linha, c.Key.Coluna)}", (c.Value.Exibido, c.Value.Formula))))
        .ToDictionary();

    private static string Raiz()
    {
        for (var diretorio = new DirectoryInfo(AppContext.BaseDirectory); diretorio is not null; diretorio = diretorio.Parent)
            if (File.Exists(Path.Combine(diretorio.FullName, "Dante.sln"))) return diretorio.FullName;
        throw new DirectoryNotFoundException("Solution não encontrada.");
    }
}
