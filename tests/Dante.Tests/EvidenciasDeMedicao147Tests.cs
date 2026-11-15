using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dante.Infrastructure.Agentes;
using Dante.ProcessProbe;
using Dante.Worker.Sessions;

namespace Dante.Tests;

// Instrumentação da medição de tokens da #147, sem CLI real nem chamada externa: FakeClaude (processo local que fala
// stream-json) e linhas no formato do Claude Code 2.1.325. Comprova captura, indisponibilidade explícita, hashes e
// releitura do disco.
public sealed class EvidenciasDeMedicao147Tests : IDisposable
{
    private const string MountsPersistentes = "/dev/nvme0n1p2 / ext4 rw 0 0\n/dev/nvme0n1p3 /srv ext4 rw 0 0\n";
    private const UnixFileMode D700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode F600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode D755 = D700 | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    // Área do teste; a raiz de evidências é um diretório novo dentro dela, como na primeira execução real.
    private readonly string area = Directory.CreateTempSubdirectory("dante147-medicao-teste-").FullName;
    private string raiz => Path.Combine(area, "evidencias");

    public void Dispose()
    {
        // Alvos deixados inacessíveis pelos testes de link voltam a 0700 para a remoção.
        if (!OperatingSystem.IsWindows())
            foreach (var d in Directory.EnumerateFileSystemEntries(area, "*", new EnumerationOptions { RecurseSubdirectories = false }))
                if (!ExecucaoDeMedicao147.EhLink(d)) File.SetUnixFileMode(d, Directory.Exists(d) ? D700 : F600);
        Directory.Delete(area, true);
    }

    // Alvo fora da execução, sem permissão de leitura: se a verificação seguisse o link, lançaria ao ler.
    private string AlvoInacessivel(string nome, bool diretorio)
    {
        var alvo = Path.Combine(area, nome);
        if (diretorio) { Directory.CreateDirectory(alvo); File.WriteAllText(Path.Combine(alvo, "manifesto.json"), "{}"); }
        else File.WriteAllText(alvo, "fora da execucao");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(alvo, UnixFileMode.None);
        return alvo;
    }

    private static void TrocarPorLink(string caminho, string alvo)
    {
        if (Directory.Exists(caminho) && !ExecucaoDeMedicao147.EhLink(caminho)) Directory.Delete(caminho, true); else File.Delete(caminho);
        File.CreateSymbolicLink(caminho, alvo);
    }

    private ExecucaoDeMedicao147 ExecucaoFechada(string arquivo = "a.txt")
    {
        var execucao = ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 1);
        execucao.GravarTexto(arquivo, "x", "um");
        execucao.Finalizar("completo", null, null, MountsPersistentes);
        Assert.Empty(ExecucaoDeMedicao147.VerificarRaiz(raiz));
        return execucao;
    }

    private static string Result(string corpo) => "{\"type\":\"result\"," + corpo + "}";

    // ---------- Contadores ----------

    // Caminho real do harness: launcher → InteractiveAgentProcess (gancho por linha) → ClaudeSessionDriver.
    [Fact]
    public async Task UsoReportadoPeloProcessoECapturadoSemAlterarOQueODriverRecebe()
    {
        var uso = new CapturaDeUsoClaude147();
        await using var driver = new ClaudeSessionDriver(new LancadorComCaptura(uso));
        await driver.StartAsync(new AgentSessionStartOptions(AppContext.BaseDirectory));
        await using var eventos = driver.ReadEventsAsync().GetAsyncEnumerator();

        await driver.StartTurnAsync("usage");
        var comUso = await TurnoAsync(eventos);
        var primeiro = new TurnoMedido147("T1", uso.Proximo(), comUso.OfType<TurnCompletedEvent>().Single().Usage);
        await driver.StartTurnAsync("pong");
        var semUso = await TurnoAsync(eventos);
        var segundo = new TurnoMedido147("T2", uso.Proximo(), semUso.OfType<TurnCompletedEvent>().Single().Usage);

        Assert.Equal(UsoDoTurno147.Valida, primeiro.Uso!.Validade);
        Assert.Equal(new ContadorReal(ContadorReal.Reportado, 12), primeiro.Uso.EntradaNova);
        Assert.Equal(new ContadorReal(ContadorReal.Reportado, 100), primeiro.Uso.CacheEscrito);
        Assert.Equal(new ContadorReal(ContadorReal.Reportado, 3000), primeiro.Uso.CacheLido);
        Assert.Equal(new ContadorReal(ContadorReal.Reportado, 7), primeiro.Uso.Saida);
        Assert.Equal(new ContadorReal(ContadorReal.Reportado, 3112), primeiro.Uso.EntradaTotal);
        Assert.Equal(new AgentTokenUsage(3112, 7, 3000), primeiro.UsoDoDriver);
        Assert.True(primeiro.ConsistenteComDriver); Assert.True(primeiro.Confiavel);
        // FakeClaude não declara modelo no init: fica indisponível, não inventado.
        Assert.Equal(new TextoReal(ContadorReal.Indisponivel, null), primeiro.Uso.Sessao!.Modelo);

        // "pong" não traz usage nem num_turns: nada medido, nada zerado.
        Assert.Equal(UsoDoTurno147.Indeterminada, segundo.Uso!.Validade);
        Assert.All(new[] { segundo.Uso.EntradaNova, segundo.Uso.CacheEscrito, segundo.Uso.CacheLido, segundo.Uso.Saida, segundo.Uso.EntradaTotal },
            c => Assert.Equal(ContadorReal.Ausente, c));
        Assert.Null(segundo.UsoDoDriver); Assert.False(segundo.Confiavel);
        Assert.Null(uso.Proximo().Uso);

        Assert.Equal("usage", comUso.OfType<MessageCompletedEvent>().Single().Text);
        Assert.Equal("pong 2", semUso.OfType<MessageCompletedEvent>().Single().Text); // FakeClaude numera o turno
    }

    [Fact]
    public void SessaoModeloPorTurnoEAcumuladoSaoSeparados()
    {
        var uso = new CapturaDeUsoClaude147();
        const string init = """{"type":"system","subtype":"init","model":"claude-opus-5-5","session_id":"s1","claude_code_version":"2.1.325","tools":["mcp__dante_brain__b","mcp__dante_brain__a"]}""";
        var r1 = Result("""
            "subtype":"success","is_error":false,"num_turns":3,"duration_ms":4100,"duration_api_ms":3900,"result_index":0,
            "first_request_input_tokens":1500,"first_request_cache_read_input_tokens":0,
            "usage":{"input_tokens":0,"cache_creation_input_tokens":1500,"cache_read_input_tokens":20480,"output_tokens":"512"},
            "model_usage_since_last_result":{"claude-opus-5-5":{"inputTokens":9,"outputTokens":512,"cacheReadInputTokens":20480,"cacheCreationInputTokens":-1}},
            "modelUsage":{"claude-opus-5-5":{"inputTokens":9,"outputTokens":512,"cacheReadInputTokens":20480,"cacheCreationInputTokens":1500}}
            """.ReplaceLineEndings(" "));
        var r2 = Result("""
            "subtype":"success","is_error":false,"num_turns":1,"result_index":1,
            "usage":{"input_tokens":4,"cache_creation_input_tokens":0,"cache_read_input_tokens":21000,"output_tokens":30},
            "model_usage_since_last_result":{"claude-opus-5-5":{"inputTokens":4,"outputTokens":30,"cacheReadInputTokens":21000,"cacheCreationInputTokens":0}},
            "modelUsage":{"claude-opus-5-5":{"inputTokens":13,"outputTokens":542,"cacheReadInputTokens":41480,"cacheCreationInputTokens":1500}}
            """.ReplaceLineEndings(" "));
        foreach (var linha in new[] { "aviso em texto puro no stderr", "{json truncado", init, r1, r2 })
            Assert.Equal(linha, uso.Observar(linha));

        var (t1, t2) = (uso.Turnos[0], uso.Turnos[1]);
        Assert.Equal(new TextoReal(ContadorReal.Reportado, "claude-opus-5-5"), t1.Sessao!.Modelo);
        Assert.Equal(new TextoReal(ContadorReal.Reportado, "2.1.325"), t1.Sessao.VersaoDaCli);
        Assert.Equal(["mcp__dante_brain__a", "mcp__dante_brain__b"], t1.Sessao.Ferramentas);
        Assert.Equal(new ContadorReal(ContadorReal.Reportado, 0), t1.IndiceDoResult);
        // Zero reportado é valor real; texto e negativo são indisponíveis.
        Assert.Equal(new ContadorReal(ContadorReal.Reportado, 0), t1.EntradaNova);
        Assert.Equal(ContadorReal.Ausente, t1.Saida);
        Assert.Equal(new ContadorReal(ContadorReal.Reportado, 21980), t1.EntradaTotal);
        Assert.Equal(new ContadorReal(ContadorReal.Reportado, 1500), t1.EntradaDaPrimeiraChamada);
        Assert.Equal(new ContadorReal(ContadorReal.Reportado, 3), t1.ChamadasNoTurno);
        Assert.Equal(ContadorReal.Ausente, t1.PorModeloNoTurno["claude-opus-5-5"].CacheEscrito);
        // Por turno vem de model_usage_since_last_result; modelUsage é acumulado e fica separado.
        Assert.Equal(new ContadorReal(ContadorReal.Reportado, 30), t2.PorModeloNoTurno["claude-opus-5-5"].Saida);
        Assert.Equal(new ContadorReal(ContadorReal.Reportado, 542), t2.PorModeloAcumulado["claude-opus-5-5"].Saida);
        Assert.Equal(ContadorReal.Ausente, t2.EntradaDaPrimeiraChamada);
        Assert.Equal(0, uso.FalhasDeLeitura);
    }

    // Claude Code zera usage em result de erro (cota, falha de início) e em result sintético sem chamada ao modelo.
    [Theory]
    [InlineData("\"subtype\":\"error_during_execution\",\"is_error\":true,\"num_turns\":0", UsoDoTurno147.ResultadoDeErro)]
    [InlineData("\"subtype\":\"success\",\"is_error\":true,\"num_turns\":1", UsoDoTurno147.ResultadoDeErro)]
    [InlineData("\"subtype\":\"success\",\"num_turns\":1", UsoDoTurno147.Indeterminada)]
    [InlineData("\"subtype\":\"success\",\"is_error\":false,\"num_turns\":0", UsoDoTurno147.SemChamadaAoModelo)]
    [InlineData("\"subtype\":\"success\",\"is_error\":false", UsoDoTurno147.Indeterminada)]
    public void ZerosDeResultSemMedicaoNuncaViramContagem(string cabecalho, string validade)
    {
        var uso = new CapturaDeUsoClaude147();
        uso.Observar(Result(cabecalho + """
            ,"usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0},
            "model_usage_since_last_result":{"m":{"inputTokens":0,"outputTokens":0,"cacheReadInputTokens":0,"cacheCreationInputTokens":0}}
            """.ReplaceLineEndings(" ")));
        var turno = Assert.Single(uso.Turnos);
        Assert.Equal(validade, turno.Validade); Assert.False(turno.Medido);
        Assert.All(new[] { turno.EntradaNova, turno.CacheEscrito, turno.CacheLido, turno.Saida, turno.PorModeloNoTurno["m"].Saida },
            c => Assert.NotEqual(ContadorReal.Reportado, c.Estado));
        Assert.False(turno.EntradaTotal.Medido);
        // O valor bruto fica auditável como nao-medido, nunca como reportado.
        Assert.Equal(new ContadorReal(ContadorReal.NaoMedido, 0), turno.Saida);
        Assert.False(new TurnoMedido147("X", new(turno, 1, 0), new AgentTokenUsage(0, 0, 0)).Confiavel);
    }

    [Fact]
    public void AssociacaoAoTurnoSoEConfiavelComExatamenteUmResult()
    {
        var uso = new CapturaDeUsoClaude147();
        string Valido(int i) => Result($"\"subtype\":\"success\",\"is_error\":false,\"num_turns\":1,\"result_index\":{i},\"usage\":{{\"input_tokens\":1,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":1}}");
        Assert.Null(uso.Proximo().Uso);
        uso.Observar(Valido(0)); uso.Observar(Valido(1));
        var dupla = uso.Proximo();
        Assert.Equal(2, dupla.ResultsPendentes); Assert.False(dupla.Unica);
        Assert.False(new TurnoMedido147("A1", dupla, new AgentTokenUsage(1, 1, 0)).Confiavel);
        Assert.Null(uso.Proximo().Uso);
        uso.Observar(Valido(2));
        var unica = uso.Proximo();
        Assert.True(unica.Unica);
        Assert.True(new TurnoMedido147("A2", unica, new AgentTokenUsage(1, 1, 0)).Confiavel);
        Assert.False(new TurnoMedido147("A2", unica, new AgentTokenUsage(2, 1, 0)).Confiavel);
        Assert.False(new TurnoMedido147("A2", unica, null).ConsistenteComDriver);
    }

    [Fact]
    public void ObservadorNuncaLancaEContaResultIlegivel()
    {
        var uso = new CapturaDeUsoClaude147();
        foreach (var linha in new[] { "{\"type\":\"result\",\"usage\":", "{\"type\":\"result\",\"num_turns\":1e999}", "{\"type\":[\"result\"]}", "{\"type\":\"init\"" })
            Assert.Equal(linha, uso.Observar(linha));
        Assert.True(uso.FalhasDeLeitura >= 2);
        var associacao = uso.Proximo();
        Assert.True(associacao.FalhasDeLeitura >= 2);
        if (associacao.Uso is not null) Assert.False(new TurnoMedido147("X", associacao, null).Confiavel);
    }

    // ---------- Evidências ----------

    [Fact]
    public void HashesDoManifestoSaoReproduziveisPelosBytesExatosEPorSha256sum()
    {
        var execucao = ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 1, new DateTimeOffset(2026, 11, 15, 13, 0, 0, TimeSpan.Zero));
        const string historico = "Pedido A\r\ncom CRLF e acentuação\n\nResposta A";
        var arquivo = execucao.GravarTexto("historico-visivel.txt", "historico-visivel-nao-integral", historico);
        execucao.GravarJson("pacote.json", "pacote", new { itens = new[] { "fonte:1@1" } });
        var hashManifesto = execucao.Finalizar("completo", "33add8fbf8411f1a45102774bfa94cfce0ce5007", true, MountsPersistentes);

        Assert.StartsWith("20261115T130000Z-retomada-r1-", execucao.Id);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(historico))), arquivo.Sha256);
        Assert.Equal(historico, File.ReadAllText(Path.Combine(execucao.Diretorio, "historico-visivel.txt")));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(execucao.Diretorio, "manifesto.json")))), hashManifesto);
        Assert.Empty(Directory.EnumerateFiles(execucao.Diretorio, "*.parcial"));
        Assert.Empty(ExecucaoDeMedicao147.VerificarRaiz(raiz));

        if (File.Exists("/usr/bin/sha256sum"))
        {
            var info = new ProcessStartInfo("/usr/bin/sha256sum") { WorkingDirectory = execucao.Diretorio, RedirectStandardOutput = true };
            info.ArgumentList.Add("-c"); info.ArgumentList.Add("--strict"); info.ArgumentList.Add("SHA256SUMS");
            using var processo = Process.Start(info)!;
            var saida = processo.StandardOutput.ReadToEnd(); processo.WaitForExit();
            Assert.Equal(0, processo.ExitCode);
            Assert.Contains("historico-visivel.txt: OK", saida); Assert.Contains("pacote.json: OK", saida);
        }
    }

    // Reinício simulado: nada em memória; manifesto, índice e conteúdo relidos só do disco e verificados.
    [Fact]
    public void EvidenciasSaoRecuperadasEVerificadasSomenteAPartirDoDisco()
    {
        string id, diretorio, hash;
        {
            var execucao = ExecucaoDeMedicao147.Iniciar(raiz, "nota", 2);
            execucao.GravarTexto("entrada-b.txt", "entrada-enviada", "contexto exato\n");
            hash = execucao.Finalizar("incompleto", null, null, MountsPersistentes);
            (id, diretorio) = (execucao.Id, execucao.Diretorio);
        }
        ExecucaoDeMedicao147.Iniciar(raiz, "nota", 1).Finalizar("completo", null, null, MountsPersistentes);

        var indice = File.ReadAllLines(Path.Combine(raiz, "indice.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).ToArray();
        Assert.Equal(2, indice.Length);
        var linha = Assert.Single(indice, l => l.GetProperty("id").GetString() == id);
        Assert.Equal(hash, linha.GetProperty("manifestoSha256").GetString());
        Assert.Equal("incompleto", linha.GetProperty("estado").GetString());

        var manifesto = ExecucaoDeMedicao147.LerManifesto(diretorio);
        Assert.Equal(id, manifesto.Id); Assert.Equal("nota", manifesto.Cenario); Assert.Equal(2, manifesto.Repeticao);
        Assert.Equal(new TextoReal(ContadorReal.Indisponivel, null), manifesto.BaseGit);
        Assert.Equal(new TextoReal(ContadorReal.Reportado, "ext4"), manifesto.SistemaDeArquivos);
        Assert.Equal("entrada-enviada", Assert.Single(manifesto.Arquivos).Papel);
        Assert.True(ExecucaoDeMedicao147.Verificar(diretorio).Integra);
        Assert.Empty(ExecucaoDeMedicao147.VerificarRaiz(raiz));
        Assert.Equal("contexto exato\n", File.ReadAllText(Path.Combine(diretorio, "entrada-b.txt")));
    }

    [Fact]
    public void AlteracaoRemocaoArquivoExtraESubdiretorioSaoDetectados()
    {
        var execucao = ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 1);
        execucao.GravarTexto("a.txt", "x", "um");
        execucao.GravarTexto("b.txt", "x", "dois");
        execucao.Finalizar("completo", null, null, MountsPersistentes);
        File.WriteAllText(Path.Combine(execucao.Diretorio, "a.txt"), "Um");
        File.Delete(Path.Combine(execucao.Diretorio, "b.txt"));
        File.WriteAllText(Path.Combine(execucao.Diretorio, "c.txt"), "extra");
        Directory.CreateDirectory(Path.Combine(execucao.Diretorio, "sub"));

        var verificacao = ExecucaoDeMedicao147.Verificar(execucao.Diretorio);
        Assert.False(verificacao.Integra);
        Assert.Equal(["alterado: a.txt", "ausente: b.txt", "nao declarado: c.txt", "subdiretorio nao declarado: sub"], verificacao.Divergencias.Order());
        Assert.NotEmpty(ExecucaoDeMedicao147.VerificarRaiz(raiz));
    }

    // Índice × manifesto × diretórios: adulteração do manifesto, execução sem fechamento, linha órfã e duplicada.
    [Fact]
    public void RaizDetectaManifestoAdulteradoExecucaoAbertaELinhasInconsistentes()
    {
        var fechada = ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 1);
        fechada.GravarTexto("a.txt", "x", "um");
        fechada.Finalizar("completo", null, null, MountsPersistentes);
        var aberta = ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 2);
        aberta.GravarTexto("parcial.txt", "x", "processo morto antes do fechamento");
        Assert.Empty(ExecucaoDeMedicao147.Verificar(fechada.Diretorio).Divergencias);

        // Manifesto regravado com o mesmo conteúdo lógico, outro byte: o índice acusa.
        var manifesto = Path.Combine(fechada.Diretorio, "manifesto.json");
        File.WriteAllText(manifesto, File.ReadAllText(manifesto) + " ");
        var indice = Path.Combine(raiz, "indice.jsonl");
        var linha = File.ReadAllLines(indice).Single();
        File.AppendAllText(indice, linha + "\n" + linha.Replace(fechada.Id, "20990101T000000Z-retomada-r9-000000000000") + "\nlixo\n");

        var divergencias = ExecucaoDeMedicao147.VerificarRaiz(raiz);
        Assert.Contains("manifesto diverge do indice: " + fechada.Id, divergencias);
        Assert.Contains("execucao sem manifesto: " + aberta.Id, divergencias);
        Assert.Contains("indice duplicado: " + fechada.Id, divergencias);
        Assert.Contains("indice sem execucao: 20990101T000000Z-retomada-r9-000000000000", divergencias);
        Assert.Contains(divergencias, d => d.StartsWith("indice linha", StringComparison.Ordinal));
    }

    // Manifesto adulterado com nome que escapa: a verificação nunca lê fora da execução.
    [Fact]
    public void ManifestoComCaminhoForaDaExecucaoNaoESeguido()
    {
        var execucao = ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 1);
        File.WriteAllText(Path.Combine(raiz, "segredo.txt"), "fora");
        execucao.Finalizar("completo", null, null, MountsPersistentes);
        var caminho = Path.Combine(execucao.Diretorio, "manifesto.json");
        var hash = ExecucaoDeMedicao147.Sha256(Encoding.UTF8.GetBytes("fora"));
        File.WriteAllText(caminho, File.ReadAllText(caminho).Replace("\"arquivos\": []",
            $"\"arquivos\": [{{\"nome\": \"../../segredo.txt\", \"papel\": \"x\", \"bytes\": 4, \"sha256\": \"{hash}\"}}]"));
        Assert.Contains("nome invalido: ../../segredo.txt", ExecucaoDeMedicao147.Verificar(execucao.Diretorio).Divergencias);
    }

    [Theory]
    [InlineData("../fora.txt")]
    [InlineData("sub/arquivo.txt")]
    [InlineData("manifesto.json")]
    [InlineData("SHA256SUMS")]
    [InlineData("Maiuscula.txt")]
    [InlineData("x.parcial")]
    public void NomesQueEscapamOuSobrescrevemOControleSaoRecusados(string nome)
    {
        var execucao = ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 1);
        Assert.Throws<ArgumentException>(() => execucao.GravarTexto(nome, "x", "y"));
        Assert.Throws<ArgumentException>(() => execucao.Reservar(nome));
        execucao.GravarTexto("unico.txt", "x", "y");
        Assert.Throws<InvalidOperationException>(() => execucao.GravarTexto("unico.txt", "x", "z"));
        Assert.Throws<InvalidOperationException>(() => execucao.Reservar("unico.txt"));
        execucao.Finalizar("completo", null, null, MountsPersistentes);
        Assert.Throws<InvalidOperationException>(() => execucao.GravarTexto("depois.txt", "x", "y"));
        Assert.Throws<InvalidOperationException>(() => execucao.Finalizar("completo", null, null, MountsPersistentes));
    }

    // Métricas do produto escritas por terceiros na execução entram no manifesto com hash.
    [Fact]
    public void ArquivoReservadoEDeclaradoNoFechamento()
    {
        var execucao = ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 1);
        var metricas = execucao.Reservar("metricas.jsonl");
        Assert.Equal(execucao.Diretorio, Path.GetDirectoryName(metricas));
        File.AppendAllText(metricas, "{\"tipo\":\"turno\"}\n");
        execucao.Reservar("nunca-escrito.jsonl");
        execucao.Finalizar("completo", null, null, MountsPersistentes);
        var declarado = Assert.Single(ExecucaoDeMedicao147.LerManifesto(execucao.Diretorio).Arquivos);
        Assert.Equal(("metricas.jsonl", "terceiro"), (declarado.Nome, declarado.Papel));
        Assert.True(ExecucaoDeMedicao147.Verificar(execucao.Diretorio).Integra);
    }

    [Fact]
    public void PermissoesSaoPrivadasDesdeACriacao()
    {
        if (OperatingSystem.IsWindows()) return;
        var nova = Path.Combine(raiz, "nova");
        var execucao = ExecucaoDeMedicao147.Iniciar(nova, "retomada", 1);
        execucao.GravarTexto("a.txt", "x", "um");
        execucao.Finalizar("completo", null, null, MountsPersistentes);
        foreach (var d in new[] { nova, Path.Combine(nova, "execucoes"), execucao.Diretorio }) Assert.Equal(D700, File.GetUnixFileMode(d));
        foreach (var f in Directory.EnumerateFiles(execucao.Diretorio).Append(Path.Combine(nova, "indice.jsonl")).Append(Path.Combine(nova, ExecucaoDeMedicao147.NomeMarcador)))
            Assert.Equal(F600, File.GetUnixFileMode(f));
    }

    // ---------- Permissões: nada preexistente é alterado ----------

    // Diretório que já existe e não foi criado como raiz dedicada (ex.: /srv, um projeto): recusado, modo e conteúdo intactos.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RaizPreexistenteNaoDedicadaERecusadaSemAlterarPermissoes(bool comConteudo)
    {
        if (OperatingSystem.IsWindows()) return;
        var existente = Directory.CreateDirectory(Path.Combine(area, "existente")).FullName;
        if (comConteudo) File.WriteAllText(Path.Combine(existente, "dado.txt"), "do usuario");
        File.SetUnixFileMode(existente, D755);
        var antes = Directory.EnumerateFileSystemEntries(existente).Order().ToArray();

        Assert.Throws<IOException>(() => ExecucaoDeMedicao147.Iniciar(existente, "retomada", 1));

        Assert.Equal(D755, File.GetUnixFileMode(existente));
        Assert.Equal(antes, Directory.EnumerateFileSystemEntries(existente).Order().ToArray());
    }

    // Ancestrais existentes não recebem chmod; só a raiz criada e o que está dentro dela são privados.
    [Fact]
    public void AncestraisExistentesMantemPermissoesEAusentesNaoSaoRestringidos()
    {
        if (OperatingSystem.IsWindows()) return;
        var pai = Directory.CreateDirectory(Path.Combine(area, "pai")).FullName;
        File.SetUnixFileMode(pai, D755);
        var nova = Path.Combine(pai, "intermediario", "issue-147");
        ExecucaoDeMedicao147.Iniciar(nova, "retomada", 1).Finalizar("completo", null, null, MountsPersistentes);

        Assert.Equal(D755, File.GetUnixFileMode(pai));
        Assert.NotEqual(UnixFileMode.None, File.GetUnixFileMode(Path.Combine(pai, "intermediario")) & UnixFileMode.OtherExecute);
        Assert.Equal(D700, File.GetUnixFileMode(nova));
    }

    // Raiz dedicada reaproveitada sem chmod; se alguém afrouxou o modo, é recusada e não "consertada".
    [Fact]
    public void RaizDedicadaEReutilizadaENuncaCorrigidaPorChmod()
    {
        if (OperatingSystem.IsWindows()) return;
        ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 1).Finalizar("completo", null, null, MountsPersistentes);
        ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 2).Finalizar("completo", null, null, MountsPersistentes);
        Assert.Empty(ExecucaoDeMedicao147.VerificarRaiz(raiz));
        Assert.Equal(D700, File.GetUnixFileMode(raiz));

        File.SetUnixFileMode(raiz, D755);
        Assert.Throws<IOException>(() => ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 3));
        Assert.Equal(D755, File.GetUnixFileMode(raiz));
        File.SetUnixFileMode(raiz, D700);
        File.SetUnixFileMode(Path.Combine(raiz, "execucoes"), D755);
        Assert.Throws<IOException>(() => ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 3));
        Assert.Equal(D755, File.GetUnixFileMode(Path.Combine(raiz, "execucoes")));
    }

    [Fact]
    public void MarcadorDaRaizComoLinkERecusado()
    {
        if (OperatingSystem.IsWindows()) return;
        ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 1).Finalizar("completo", null, null, MountsPersistentes);
        var alvo = AlvoInacessivel("marcador-alheio", diretorio: false);
        TrocarPorLink(Path.Combine(raiz, ExecucaoDeMedicao147.NomeMarcador), alvo);
        Assert.Throws<IOException>(() => ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 2));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/srv")]
    [InlineData("/mnt")]
    [InlineData("/home")]
    [InlineData("/var")]
    [InlineData("/opt")]
    public void DiretorioGenericoDoSistemaERecusado(string caminho) =>
        Assert.Throws<InvalidOperationException>(() => RaizDeEvidencias147.Validar(caminho, MountsPersistentes));

    [Fact]
    public void DiretorioGenericoDoUsuarioEPontoDeMontagemSaoRecusados()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var caminho in new[] { home, Path.Combine(home, ".local"), Path.Combine(home, ".local", "share"), Path.Combine(home, ".cache"),
                     Path.Combine(home, ".local", "share", "dante-tests") })
            Assert.Throws<InvalidOperationException>(() => RaizDeEvidencias147.Validar(caminho, MountsPersistentes));
        var comMontagem = MountsPersistentes + "/dev/sdb1 /srv/dados ext4 rw 0 0\n";
        Assert.Throws<InvalidOperationException>(() => RaizDeEvidencias147.Validar("/srv/dados", comMontagem));
        Assert.Equal("/srv/dados/issue-147", RaizDeEvidencias147.Validar("/srv/dados/issue-147", comMontagem));
    }

    // Arquivo reservado (métricas do produto) plantado como link: recusado, alvo sem chmod e sem leitura.
    [Fact]
    public void ArquivoReservadoComoLinkERecusadoSemAlterarOAlvo()
    {
        if (OperatingSystem.IsWindows()) return;
        var execucao = ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 1);
        var alvo = Path.Combine(area, "alvo-metricas");
        File.WriteAllText(alvo, "de terceiros");
        File.SetUnixFileMode(alvo, D755 & ~UnixFileMode.UserExecute & ~UnixFileMode.GroupExecute & ~UnixFileMode.OtherExecute);
        var modo = File.GetUnixFileMode(alvo);
        File.CreateSymbolicLink(execucao.Reservar("metricas.jsonl"), alvo);
        Assert.Throws<IOException>(() => execucao.Finalizar("completo", null, null, MountsPersistentes));
        Assert.Equal(modo, File.GetUnixFileMode(alvo));
    }

    [Fact]
    public void IndiceComoLinkNaoRecebeEscrita()
    {
        if (OperatingSystem.IsWindows()) return;
        ExecucaoFechada();
        var alvo = Path.Combine(area, "indice-alheio");
        File.WriteAllText(alvo, "intacto");
        TrocarPorLink(Path.Combine(raiz, "indice.jsonl"), alvo);
        var outra = ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 2);
        Assert.Throws<IOException>(() => outra.Finalizar("completo", null, null, MountsPersistentes));
        Assert.Equal("intacto", File.ReadAllText(alvo));
    }

    // ---------- Verificação: nenhum caminho de controle é seguido ----------

    [Fact]
    public void ManifestoComoLinkNaoELido()
    {
        if (OperatingSystem.IsWindows()) return;
        var execucao = ExecucaoFechada();
        TrocarPorLink(Path.Combine(execucao.Diretorio, "manifesto.json"), AlvoInacessivel("manifesto-alheio", diretorio: false));
        Assert.Equal(["link recusado: manifesto.json"], ExecucaoDeMedicao147.Verificar(execucao.Diretorio).Divergencias);
        Assert.Throws<IOException>(() => ExecucaoDeMedicao147.LerManifesto(execucao.Diretorio));
        Assert.Contains(execucao.Id + ": link recusado: manifesto.json", ExecucaoDeMedicao147.VerificarRaiz(raiz));
    }

    [Fact]
    public void Sha256sumsComoLinkNaoELido()
    {
        if (OperatingSystem.IsWindows()) return;
        var execucao = ExecucaoFechada();
        TrocarPorLink(Path.Combine(execucao.Diretorio, "SHA256SUMS"), AlvoInacessivel("somas-alheias", diretorio: false));
        Assert.Contains("link recusado: SHA256SUMS", ExecucaoDeMedicao147.Verificar(execucao.Diretorio).Divergencias);
    }

    [Fact]
    public void ArquivoDeclaradoComoLinkNaoELido()
    {
        if (OperatingSystem.IsWindows()) return;
        var execucao = ExecucaoFechada();
        TrocarPorLink(Path.Combine(execucao.Diretorio, "a.txt"), AlvoInacessivel("dado-alheio", diretorio: false));
        Assert.Equal(["link recusado: a.txt"], ExecucaoDeMedicao147.Verificar(execucao.Diretorio).Divergencias);
    }

    [Fact]
    public void LinkNaoDeclaradoNaExecucaoEReportadoSemSeguir()
    {
        if (OperatingSystem.IsWindows()) return;
        var execucao = ExecucaoFechada();
        Directory.CreateSymbolicLink(Path.Combine(execucao.Diretorio, "extra"), AlvoInacessivel("dir-alheio", diretorio: true));
        Assert.Equal(["link nao declarado: extra"], ExecucaoDeMedicao147.Verificar(execucao.Diretorio).Divergencias);
    }

    [Fact]
    public void DiretorioDeExecucaoComoLinkNaoESeguido()
    {
        if (OperatingSystem.IsWindows()) return;
        var execucao = ExecucaoFechada();
        TrocarPorLink(execucao.Diretorio, AlvoInacessivel("execucao-alheia", diretorio: true));
        Assert.Equal(["link recusado: diretorio da execucao"], ExecucaoDeMedicao147.Verificar(execucao.Diretorio).Divergencias);
        var divergencias = ExecucaoDeMedicao147.VerificarRaiz(raiz);
        Assert.Contains("link recusado: execucao " + execucao.Id, divergencias);
        Assert.DoesNotContain(divergencias, d => d.Contains("manifesto diverge", StringComparison.Ordinal));
    }

    [Fact]
    public void DiretorioExecucoesComoLinkNaoESeguidoNaVerificacao()
    {
        if (OperatingSystem.IsWindows()) return;
        ExecucaoFechada();
        TrocarPorLink(Path.Combine(raiz, "execucoes"), AlvoInacessivel("execucoes-alheias", diretorio: true));
        Assert.Equal(["link recusado: execucoes"], ExecucaoDeMedicao147.VerificarRaiz(raiz));
    }

    [Fact]
    public void IndiceComoLinkNaoELido()
    {
        if (OperatingSystem.IsWindows()) return;
        ExecucaoFechada();
        TrocarPorLink(Path.Combine(raiz, "indice.jsonl"), AlvoInacessivel("indice-inacessivel", diretorio: false));
        var divergencias = ExecucaoDeMedicao147.VerificarRaiz(raiz);
        Assert.Contains("link recusado: indice.jsonl", divergencias);
    }

    [Fact]
    public void RaizComoLinkNaoESeguidaNaVerificacao()
    {
        if (OperatingSystem.IsWindows()) return;
        var alvo = AlvoInacessivel("raiz-alheia", diretorio: true);
        var link = Path.Combine(area, "raiz-link");
        Directory.CreateSymbolicLink(link, alvo);
        Assert.Equal(["link recusado: raiz"], ExecucaoDeMedicao147.VerificarRaiz(link));
        Assert.Throws<IOException>(() => ExecucaoDeMedicao147.Iniciar(link, "retomada", 1));
    }

    [Fact]
    public void DiretorioDeExecucoesComoLinkSimbolicoERecusado()
    {
        if (OperatingSystem.IsWindows()) return;
        ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 1).Finalizar("completo", null, null, MountsPersistentes);
        var alvo = Directory.CreateDirectory(Path.Combine(area, "alvo")).FullName;
        TrocarPorLink(Path.Combine(raiz, "execucoes"), alvo);
        Assert.Throws<IOException>(() => ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 2));
        Assert.Empty(Directory.EnumerateFileSystemEntries(alvo));
    }

    // ---------- Raiz persistente ----------

    [Fact]
    public void RaizEmTemporarioOuVolatilOuDoWorkerERecusada()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var proibido in new[] { "/tmp/x", Path.Combine(Path.GetTempPath(), "y"), "/dev/shm/z", "/var/tmp/w", "/run/user/1000/w",
                     Path.Combine(home, ".local", "share", "dante", "evidencias"), Path.Combine(home, ".config", "dante") })
            Assert.Throws<InvalidOperationException>(() => RaizDeEvidencias147.Validar(proibido, MountsPersistentes));

        var volatil = MountsPersistentes + "tmpfs /srv/ram tmpfs rw 0 0\ntmpfs /srv/com\\040espaco tmpfs rw 0 0\n";
        Assert.Throws<InvalidOperationException>(() => RaizDeEvidencias147.Validar("/srv/ram/ev", volatil));
        Assert.Throws<InvalidOperationException>(() => RaizDeEvidencias147.Validar("/srv/com espaco/ev", volatil));
        Assert.Equal("/srv/dados/ev", RaizDeEvidencias147.Validar("/srv/dados/ev/", volatil));
        Assert.Equal("ext4", RaizDeEvidencias147.TipoDoSistemaDeArquivos("/srv/ramal", volatil));
        Assert.Equal("tmpfs", RaizDeEvidencias147.TipoDoSistemaDeArquivos("/srv/ram", volatil));
    }

    // Um link num lugar permitido que aponta para temporário é recusado; o caminho real é o usado.
    [Fact]
    public void LinkSimbolicoParaTemporarioERecusadoECaminhoRealEDevolvido()
    {
        if (OperatingSystem.IsWindows() || RaizDeEvidencias147.CaminhoReal(AppContext.BaseDirectory).StartsWith("/tmp/", StringComparison.Ordinal)) return;
        // Fora de /tmp sem tocar no home: saída de build do próprio teste (ignorada pelo Git).
        var permitido = Path.Combine(AppContext.BaseDirectory, "dante147-teste-link-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(permitido);
        try
        {
            var link = Path.Combine(permitido, "ev");
            Directory.CreateSymbolicLink(link, raiz);
            Assert.Equal(RaizDeEvidencias147.CaminhoReal(raiz), RaizDeEvidencias147.CaminhoReal(Path.Combine(link)));
            Assert.Equal(Path.Combine(RaizDeEvidencias147.CaminhoReal(raiz), "a", "b"), RaizDeEvidencias147.CaminhoReal(Path.Combine(link, "a", "b")));
            Assert.Throws<InvalidOperationException>(() => RaizDeEvidencias147.Validar(Path.Combine(link, "issue-147"), MountsPersistentes));

            var real = Directory.CreateDirectory(Path.Combine(permitido, "real")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(permitido, "relativo"), "real");
            Assert.Equal(Path.Combine(real, "x"), RaizDeEvidencias147.Validar(Path.Combine(permitido, "relativo", "x"), MountsPersistentes));
        }
        finally { Directory.Delete(permitido, true); }
    }

    [Fact]
    public void RaizPadraoFicaEmDadosDoUsuarioForaDoWorker()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DANTE_147_EVIDENCIAS"))) return;
        var padrao = RaizDeEvidencias147.Padrao();
        Assert.EndsWith(Path.Combine("dante-tests", "issue-147"), padrao);
        Assert.Equal(RaizDeEvidencias147.CaminhoReal(padrao), RaizDeEvidencias147.Validar(padrao, MountsPersistentes));
    }

    // ---------- Integração com o E2E ----------

    [Fact]
    public void PacoteExportadoPrecisaSerOPrefixoExatoDoTextoEnviado()
    {
        const string pacote = "[contexto do Brain]\nitem\n";
        Assert.True(PacoteInjetado147.Confere(pacote + "[fim do contexto do Brain]\n\nMensagem do usuário:\noi", pacote));
        Assert.False(PacoteInjetado147.Confere(pacote.TrimEnd('\n') + "\r\n[fim]", pacote));
        Assert.False(PacoteInjetado147.Confere("Mensagem do usuário:\noi", pacote));
        Assert.False(PacoteInjetado147.Confere("qualquer", ""));
    }

    // Fechamento usado pelo E2E: inclui results não associados (falha no meio de um turno) e marca indisponível.
    [Fact]
    public void FechamentoDoHarnessPreservaResultsNaoAssociadosEManifestoIntegro()
    {
        var execucao = ExecucaoDeMedicao147.Iniciar(raiz, "retomada", 1);
        var a = new CapturaDeUsoClaude147();
        a.Observar(Result("\"subtype\":\"success\",\"is_error\":false,\"num_turns\":1,\"usage\":{\"input_tokens\":5,\"cache_creation_input_tokens\":1,\"cache_read_input_tokens\":2,\"output_tokens\":3}"));
        var turnos = new List<TurnoMedido147> { new("A1", a.Proximo(), new AgentTokenUsage(8, 3, 2)) };
        // Turno A2 morreu depois do result e antes do TurnCompletedEvent: o result fica só na sessão.
        a.Observar(Result("\"subtype\":\"success\",\"is_error\":false,\"num_turns\":2,\"usage\":{\"input_tokens\":7,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":8,\"output_tokens\":9}"));

        Issue147BrainIndependenteTests.FecharExecucao(execucao, turnos, new() { ["A"] = a, ["B"] = new() }, completa: false, mounts: MountsPersistentes);

        Assert.True(ExecucaoDeMedicao147.Verificar(execucao.Diretorio).Integra);
        Assert.Equal("incompleto", ExecucaoDeMedicao147.LerManifesto(execucao.Diretorio).Estado);
        using var uso = JsonDocument.Parse(File.ReadAllText(Path.Combine(execucao.Diretorio, "uso-real.json")));
        var turno = Assert.Single(uso.RootElement.GetProperty("turnos").EnumerateArray());
        Assert.True(turno.GetProperty("confiavel").GetBoolean());
        Assert.Equal(8, turno.GetProperty("uso").GetProperty("entradaTotal").GetProperty("valor").GetInt64());
        var resultsA = uso.RootElement.GetProperty("sessoes").GetProperty("A").GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(2, resultsA.Length);
        Assert.Equal(15, resultsA[1].GetProperty("entradaTotal").GetProperty("valor").GetInt64());
        Assert.Empty(uso.RootElement.GetProperty("sessoes").GetProperty("B").GetProperty("results").EnumerateArray());
    }

    private static async Task<List<AgentEvent>> TurnoAsync(IAsyncEnumerator<AgentEvent> eventos)
    {
        using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var turno = new List<AgentEvent>();
        do
        {
            Assert.True(await eventos.MoveNextAsync().AsTask().WaitAsync(limite.Token));
            turno.Add(eventos.Current);
        } while (turno[^1] is not TurnCompletedEvent);
        return turno;
    }

    // Mesmo gancho do LancadorSomenteBrain do E2E, sobre o FakeClaude local.
    private sealed class LancadorComCaptura(CapturaDeUsoClaude147 uso) : IInteractiveAgentProcessLauncher
    {
        private static readonly string Probe = typeof(ProbeMarker).Assembly.Location;
        private static readonly string RuntimeConfig = Path.Combine(AppContext.BaseDirectory, "Dante.Tests.runtimeconfig.json");
        private readonly InteractiveAgentProcessLauncher inner = new(new DotnetResolver(), new OpcoesContencaoAgentes { Modo = ModoContencaoAgentes.Compatibilidade });

        public Task<InteractiveAgentProcess> StartAsync(AgentProcessRequest request, Func<string, string>? redactOutput = null, CancellationToken cancellationToken = default) =>
            inner.StartAsync(request with { Arguments = ["exec", "--runtimeconfig", RuntimeConfig, Probe, "fake-claude", .. request.Arguments] },
                linha => uso.Observar(redactOutput is null ? linha : redactOutput(linha)), cancellationToken);
    }

    private sealed class DotnetResolver : IAgentExecutableResolver
    {
        public string? Resolve(AgentKind agent) =>
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("dotnet host must be on PATH to run process tests.");
    }
}
