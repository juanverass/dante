using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dante.Worker.Sessions;

namespace Dante.Tests;

// Medição de tokens da #147: só contadores reportados pelo provedor. Ausente é "indisponivel", nunca zero nem estimativa.
// "nao-medido" conserva o valor bruto de um result de erro ou sintético (sem chamada à API): Claude Code zera usage
// nesses casos, e o zero não é medição.
internal sealed record ContadorReal(string Estado, long? Valor)
{
    internal const string Reportado = "reportado";
    internal const string Indisponivel = "indisponivel";
    internal const string NaoMedido = "nao-medido";
    internal static readonly ContadorReal Ausente = new(Indisponivel, null);

    internal bool Medido => Estado == Reportado;

    internal static ContadorReal De(JsonNode? no) =>
        no is JsonValue valor && valor.GetValueKind() == JsonValueKind.Number && valor.TryGetValue<long>(out var n) && n >= 0
            ? new(Reportado, n) : Ausente;

    internal ContadorReal Invalidado() => Medido ? new(NaoMedido, Valor) : this;

    internal static ContadorReal Somar(params ContadorReal[] partes) =>
        partes.All(x => x.Medido) ? new(Reportado, partes.Sum(x => x.Valor!.Value)) : Ausente;
}

internal sealed record TextoReal(string Estado, string? Valor)
{
    internal static TextoReal De(string? valor) =>
        string.IsNullOrWhiteSpace(valor) ? new(ContadorReal.Indisponivel, null) : new(ContadorReal.Reportado, valor);
}

internal sealed record UsoPorModelo147(ContadorReal EntradaNova, ContadorReal CacheEscrito, ContadorReal CacheLido, ContadorReal Saida)
{
    internal static UsoPorModelo147 De(JsonObject m) => new(ContadorReal.De(m["inputTokens"]), ContadorReal.De(m["cacheCreationInputTokens"]),
        ContadorReal.De(m["cacheReadInputTokens"]), ContadorReal.De(m["outputTokens"]));

    internal UsoPorModelo147 Invalidado() => new(EntradaNova.Invalidado(), CacheEscrito.Invalidado(), CacheLido.Invalidado(), Saida.Invalidado());
}

// O system/init da sessão (Claude Code emite um por consulta): modelo pedido, versão da CLI e ferramentas expostas.
internal sealed record SessaoClaude147(TextoReal Modelo, TextoReal IdDaSessao, TextoReal VersaoDaCli, IReadOnlyList<string> Ferramentas);

// Uma mensagem "result" do stream-json do Claude Code (semântica conferida no Claude Code 2.1.325):
// - usage: zerado no início de cada turno e somado em cada chamada do loop principal daquele turno. Com ferramentas há
//   várias chamadas e o contexto é recontado em cada uma: é entrada processada no turno, não tamanho do prompt.
// - model_usage_since_last_result: por modelo, chamadas contadas desde o result anterior (inclui subagentes/internas).
// - modelUsage: total ACUMULADO da sessão; nunca somar entre turnos.
// - first_request_*: campo interno opcional com a entrada da primeira chamada do turno (tamanho do prompt).
// EntradaTotal = entrada nova + cache escrito + cache lido, somente quando os três foram medidos.
internal sealed record UsoDoTurno147(
    int Ordem, ContadorReal IndiceDoResult, string? Subtipo, bool? Erro, string Validade, SessaoClaude147? Sessao,
    ContadorReal EntradaNova, ContadorReal CacheEscrito, ContadorReal CacheLido, ContadorReal Saida, ContadorReal EntradaTotal,
    ContadorReal EntradaDaPrimeiraChamada, ContadorReal CacheLidoDaPrimeiraChamada,
    IReadOnlyDictionary<string, UsoPorModelo147> PorModeloNoTurno, IReadOnlyDictionary<string, UsoPorModelo147> PorModeloAcumulado,
    ContadorReal ChamadasNoTurno, ContadorReal DuracaoMs, ContadorReal DuracaoApiMs)
{
    internal const string Valida = "valida";
    internal const string ResultadoDeErro = "resultado-de-erro";
    internal const string SemChamadaAoModelo = "sem-chamada-ao-modelo";
    internal const string Indeterminada = "indeterminada";

    internal bool Medido => Validade == Valida;

    internal static UsoDoTurno147 De(JsonObject resultado, int ordem, SessaoClaude147? sessao)
    {
        var subtipo = CapturaDeUsoClaude147.Texto(resultado["subtype"]);
        bool? erro = resultado["is_error"] is JsonValue e && e.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? e.GetValue<bool>() : null;
        var chamadas = ContadorReal.De(resultado["num_turns"]);
        // Sintéticos (turno cortado, resposta local) saem como success com num_turns 0 e usage zerado.
        var validade = subtipo != "success" || erro == true ? ResultadoDeErro
            : erro is null || !chamadas.Medido ? Indeterminada
            : chamadas.Valor == 0 ? SemChamadaAoModelo : Valida;
        var invalida = validade != Valida;
        ContadorReal C(JsonNode? no) => invalida ? ContadorReal.De(no).Invalidado() : ContadorReal.De(no);
        IReadOnlyDictionary<string, UsoPorModelo147> Modelos(JsonNode? no)
        {
            var mapa = new SortedDictionary<string, UsoPorModelo147>(StringComparer.Ordinal);
            if (no is JsonObject modelos)
                foreach (var (modelo, m) in modelos)
                    if (m is JsonObject valores) mapa[modelo] = invalida ? UsoPorModelo147.De(valores).Invalidado() : UsoPorModelo147.De(valores);
            return mapa;
        }
        var uso = resultado["usage"] as JsonObject;
        var entrada = C(uso?["input_tokens"]);
        var escrito = C(uso?["cache_creation_input_tokens"]);
        var lido = C(uso?["cache_read_input_tokens"]);
        return new(ordem, ContadorReal.De(resultado["result_index"]), subtipo, erro, validade, sessao,
            entrada, escrito, lido, C(uso?["output_tokens"]), ContadorReal.Somar(entrada, escrito, lido),
            C(resultado["first_request_input_tokens"]), C(resultado["first_request_cache_read_input_tokens"]),
            Modelos(resultado["model_usage_since_last_result"]), Modelos(resultado["modelUsage"]),
            chamadas, ContadorReal.De(resultado["duration_ms"]), ContadorReal.De(resultado["duration_api_ms"]));
    }
}

// Observa as linhas do processo da CLI (gancho redactOutput do launcher) sem alterá-las e sem nunca lançar:
// uma exceção aqui derrubaria a leitura do processo. Guarda só contadores e metadados; nenhum texto de mensagem.
internal sealed class CapturaDeUsoClaude147
{
    private readonly Lock gate = new();
    private readonly List<UsoDoTurno147> turnos = [];
    private SessaoClaude147? sessao;
    private int consumidos;
    private int falhas;

    internal IReadOnlyList<UsoDoTurno147> Turnos { get { lock (gate) return turnos.ToArray(); } }
    internal SessaoClaude147? Sessao { get { lock (gate) return sessao; } }
    // Linhas que pareciam result/init mas não puderam ser lidas: um result perdido desalinha a associação.
    internal int FalhasDeLeitura { get { lock (gate) return falhas; } }

    internal string Observar(string linha)
    {
        // Filtro barato: só result e init interessam; o resto (deltas, ferramentas) nem é parseado.
        if (!linha.Contains("\"result\"", StringComparison.Ordinal) && !linha.Contains("\"init\"", StringComparison.Ordinal)) return linha;
        try { Registrar(linha); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            if (linha.AsSpan().TrimStart().StartsWith("{")) lock (gate) falhas++;
        }
        return linha;
    }

    private void Registrar(string linha)
    {
        if (!linha.AsSpan().TrimStart().StartsWith("{") || JsonNode.Parse(linha) is not JsonObject mensagem) return;
        lock (gate)
        {
            switch (Texto(mensagem["type"]))
            {
                case "system" when Texto(mensagem["subtype"]) == "init":
                    sessao = new(TextoReal.De(Texto(mensagem["model"])), TextoReal.De(Texto(mensagem["session_id"])),
                        TextoReal.De(Texto(mensagem["claude_code_version"])),
                        (mensagem["tools"] as JsonArray ?? []).Select(x => Texto(x)).OfType<string>().Order(StringComparer.Ordinal).ToArray());
                    break;
                case "result":
                    turnos.Add(UsoDoTurno147.De(mensagem, turnos.Count + 1, sessao)); break;
            }
        }
    }

    // O result de um turno passa por aqui antes de o driver emitir TurnCompletedEvent. Associa exatamente um result
    // ao turno; zero ou mais de um pendente é anomalia registrada, não resolvida por suposição.
    internal AssociacaoDeUso147 Proximo()
    {
        lock (gate)
        {
            var pendentes = turnos.Count - consumidos;
            if (pendentes == 0) return new(null, 0, falhas);
            var uso = turnos[consumidos];
            consumidos = turnos.Count;
            return new(uso, pendentes, falhas);
        }
    }

    internal static string? Texto(JsonNode? no) => no is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}

internal sealed record AssociacaoDeUso147(UsoDoTurno147? Uso, int ResultsPendentes, int FalhasDeLeitura)
{
    internal bool Unica => Uso is not null && ResultsPendentes == 1;
}

internal sealed record TurnoMedido147(string Rotulo, AssociacaoDeUso147 Associacao, AgentTokenUsage? UsoDoDriver)
{
    internal UsoDoTurno147? Uso => Associacao.Uso;

    // O driver de produção converte cache ausente em 0 ao somar a entrada; por isso a comparação só vale com os três medidos.
    internal bool ConsistenteComDriver => Uso switch
    {
        null => UsoDoDriver is null,
        { Medido: false } => true, // result sem medição: nada a comparar
        _ => (!Uso.EntradaTotal.Medido || UsoDoDriver?.InputTokens == Uso.EntradaTotal.Valor)
             && (!Uso.Saida.Medido || UsoDoDriver?.OutputTokens == Uso.Saida.Valor)
             && (!Uso.CacheLido.Medido || UsoDoDriver?.CachedInputTokens == Uso.CacheLido.Valor)
    };

    internal bool Confiavel => Associacao.Unica && Associacao.FalhasDeLeitura == 0 && Uso!.Medido && ConsistenteComDriver;
}

internal sealed record ArquivoDeEvidencia147(string Nome, string Papel, long Bytes, string Sha256);

internal sealed record ManifestoDeExecucao147(
    string Versao, string Id, string Cenario, int Repeticao, string Estado, DateTimeOffset CriadoEm, DateTimeOffset FinalizadoEm,
    TextoReal BaseGit, bool? ArvoreGitLimpa, TextoReal SistemaDeArquivos, IReadOnlyList<ArquivoDeEvidencia147> Arquivos);

internal sealed record VerificacaoDeExecucao147(string Id, IReadOnlyList<string> Divergencias)
{
    internal bool Integra => Divergencias.Count == 0;
}

// Evidências persistentes de uma execução: um diretório por execução, escrita atômica com fsync e permissões 0700/0600
// desde a criação, manifesto com SHA-256 por arquivo, SHA256SUMS (sha256sum -c) e índice global append-only.
internal sealed partial class ExecucaoDeMedicao147
{
    internal const string Versao = "issue147-medicao@2";
    internal const string NomeManifesto = "manifesto.json";
    internal const string NomeSomas = "SHA256SUMS";
    internal const string NomeIndice = "indice.jsonl";
    private const UnixFileMode Diretorio0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Arquivo0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly List<ArquivoDeEvidencia147> arquivos = [];
    private readonly HashSet<string> reservados = new(StringComparer.Ordinal);
    private readonly DateTimeOffset criadoEm;
    private readonly string raiz;
    private bool finalizada;

    private ExecucaoDeMedicao147(string raiz, string id, string cenario, int repeticao, DateTimeOffset criadoEm)
    {
        (this.raiz, Id, Cenario, Repeticao, this.criadoEm) = (raiz, id, cenario, repeticao, criadoEm);
        Diretorio = Path.Combine(raiz, "execucoes", id);
    }

    internal string Id { get; }
    internal string Cenario { get; }
    internal int Repeticao { get; }
    internal string Diretorio { get; }
    internal bool Finalizada => finalizada;

    internal static ExecucaoDeMedicao147 Iniciar(string raiz, string cenario, int repeticao, DateTimeOffset? agora = null)
    {
        if (!NomeValido().IsMatch(cenario)) throw new ArgumentException("Cenário inválido.", nameof(cenario));
        var em = (agora ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var id = $"{em:yyyyMMdd'T'HHmmss'Z'}-{cenario}-r{repeticao}-{Guid.NewGuid().ToString("N")[..12]}";
        var execucao = new ExecucaoDeMedicao147(Path.GetFullPath(raiz), id, cenario, repeticao, em);
        CriarPrivado(execucao.raiz);
        CriarPrivado(Path.Combine(execucao.raiz, "execucoes"));
        // Diretório novo e exclusivo: uma execução nunca reaproveita outra.
        if (Directory.Exists(execucao.Diretorio) || File.Exists(execucao.Diretorio)) throw new IOException("Execução já existe: " + id);
        CriarPrivado(execucao.Diretorio);
        return execucao;
    }

    // Texto exatamente como enviado/recebido: UTF-8 sem BOM, sem normalização de quebras de linha.
    internal ArquivoDeEvidencia147 GravarTexto(string nome, string papel, string conteudo) => Gravar(nome, papel, Utf8.GetBytes(conteudo));

    internal ArquivoDeEvidencia147 GravarJson(string nome, string papel, object valor) =>
        Gravar(nome, papel, JsonSerializer.SerializeToUtf8Bytes(valor, Json));

    // Caminho dentro da execução para um arquivo escrito por terceiros (métricas do produto); declarado no fechamento.
    internal string Reservar(string nome)
    {
        ValidarNovo(nome);
        reservados.Add(nome);
        return Path.Combine(Diretorio, nome);
    }

    private ArquivoDeEvidencia147 Gravar(string nome, string papel, byte[] conteudo)
    {
        ValidarNovo(nome);
        EscreverAtomico(Path.Combine(Diretorio, nome), conteudo);
        return Declarar(nome, papel, conteudo);
    }

    private ArquivoDeEvidencia147 Declarar(string nome, string papel, byte[] conteudo)
    {
        var arquivo = new ArquivoDeEvidencia147(nome, papel, conteudo.LongLength, Sha256(conteudo));
        arquivos.Add(arquivo);
        return arquivo;
    }

    private void ValidarNovo(string nome)
    {
        if (finalizada) throw new InvalidOperationException("Execução já finalizada.");
        if (!NomeValido().IsMatch(nome) || nome is NomeManifesto or NomeSomas || nome.EndsWith(".parcial", StringComparison.Ordinal))
            throw new ArgumentException("Nome de evidência inválido.", nameof(nome));
        if (arquivos.Any(x => x.Nome == nome) || reservados.Contains(nome)) throw new InvalidOperationException("Evidência já gravada: " + nome);
    }

    // Manifesto por último: sua presença marca a execução como fechada. Retorna o SHA-256 do manifesto.
    internal string Finalizar(string estado, string? baseGit, bool? arvoreLimpa = null, string? mounts = null)
    {
        if (finalizada) throw new InvalidOperationException("Execução já finalizada.");
        foreach (var nome in reservados)
        {
            var caminho = Path.Combine(Diretorio, nome);
            if (!File.Exists(caminho)) continue;
            Restringir(caminho, Arquivo0600);
            Declarar(nome, "terceiro", File.ReadAllBytes(caminho));
        }
        finalizada = true;
        // Reconfere no fechamento: a montagem pode ter mudado desde a validação inicial.
        var tipo = RaizDeEvidencias147.TipoDoSistemaDeArquivos(Diretorio, mounts ?? RaizDeEvidencias147.LerMounts());
        var manifesto = new ManifestoDeExecucao147(Versao, Id, Cenario, Repeticao, estado, criadoEm, DateTimeOffset.UtcNow,
            TextoReal.De(baseGit), arvoreLimpa, TextoReal.De(tipo), arquivos.OrderBy(x => x.Nome, StringComparer.Ordinal).ToArray());
        EscreverAtomico(Path.Combine(Diretorio, NomeSomas), Utf8.GetBytes(Somas(manifesto)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifesto, Json);
        EscreverAtomico(Path.Combine(Diretorio, NomeManifesto), bytes);
        var hash = Sha256(bytes);
        var linha = JsonSerializer.Serialize(new { id = Id, cenario = Cenario, repeticao = Repeticao, estado, manifestoSha256 = hash, criadoEm }) + "\n";
        var opcoes = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) opcoes.UnixCreateMode = Arquivo0600;
        using (var indice = new FileStream(Path.Combine(raiz, NomeIndice), opcoes))
        {
            indice.Write(Utf8.GetBytes(linha));
            indice.Flush(true);
        }
        return hash;
    }

    internal static ManifestoDeExecucao147 LerManifesto(string diretorio) =>
        JsonSerializer.Deserialize<ManifestoDeExecucao147>(File.ReadAllBytes(Path.Combine(diretorio, NomeManifesto)), Json)
        ?? throw new InvalidDataException("Manifesto vazio.");

    // Recalcula tudo a partir do disco: arquivo ausente, alterado, não declarado, subdiretório, nome inválido no
    // manifesto (nunca segue caminho fora da execução) e SHA256SUMS divergente.
    internal static VerificacaoDeExecucao147 Verificar(string diretorio)
    {
        ManifestoDeExecucao147 manifesto;
        try { manifesto = LerManifesto(diretorio); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        { return new(Path.GetFileName(diretorio), ["manifesto ilegivel: " + e.GetType().Name]); }
        var divergencias = new List<string>();
        if (manifesto.Id != Path.GetFileName(Path.TrimEndingDirectorySeparator(diretorio))) divergencias.Add("id diverge do diretorio");
        foreach (var a in manifesto.Arquivos)
        {
            if (!NomeValido().IsMatch(a.Nome) || a.Nome is NomeManifesto or NomeSomas) { divergencias.Add("nome invalido: " + a.Nome); continue; }
            var caminho = Path.Combine(diretorio, a.Nome);
            if (!File.Exists(caminho) || new FileInfo(caminho).LinkTarget is not null) { divergencias.Add("ausente: " + a.Nome); continue; }
            var bytes = File.ReadAllBytes(caminho);
            if (bytes.LongLength != a.Bytes || Sha256(bytes) != a.Sha256) divergencias.Add("alterado: " + a.Nome);
        }
        var declarados = manifesto.Arquivos.Select(x => x.Nome).Append(NomeManifesto).Append(NomeSomas).ToHashSet(StringComparer.Ordinal);
        divergencias.AddRange(Directory.EnumerateFiles(diretorio).Select(Path.GetFileName).Where(n => !declarados.Contains(n!)).Select(n => "nao declarado: " + n));
        divergencias.AddRange(Directory.EnumerateDirectories(diretorio).Select(d => "subdiretorio nao declarado: " + Path.GetFileName(d)));
        var caminhoSomas = Path.Combine(diretorio, NomeSomas);
        if (!File.Exists(caminhoSomas) || File.ReadAllText(caminhoSomas, Utf8) != Somas(manifesto)) divergencias.Add("divergente: " + NomeSomas);
        return new(manifesto.Id, divergencias);
    }

    // Raiz inteira: cada linha do índice aponta para uma execução cujo manifesto tem aquele hash; cada execução
    // tem exatamente uma linha; execução sem manifesto (processo morto antes do fechamento) é reportada.
    internal static IReadOnlyList<string> VerificarRaiz(string raiz)
    {
        var divergencias = new List<string>();
        var execucoes = Path.Combine(raiz, "execucoes");
        var linhas = new Dictionary<string, string>(StringComparer.Ordinal);
        var caminhoIndice = Path.Combine(raiz, NomeIndice);
        foreach (var (texto, numero) in (File.Exists(caminhoIndice) ? File.ReadAllLines(caminhoIndice, Utf8) : []).Select((l, i) => (l, i + 1)))
        {
            string? id = null, hash = null;
            try { using var doc = JsonDocument.Parse(texto); id = doc.RootElement.GetProperty("id").GetString(); hash = doc.RootElement.GetProperty("manifestoSha256").GetString(); }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
            if (id is null || hash is null || !IdValido().IsMatch(id)) { divergencias.Add($"indice linha {numero} invalida"); continue; }
            if (!linhas.TryAdd(id, hash)) { divergencias.Add("indice duplicado: " + id); continue; }
            var manifesto = Path.Combine(execucoes, id, NomeManifesto);
            if (!File.Exists(manifesto)) divergencias.Add("indice sem execucao: " + id);
            else if (Sha256(File.ReadAllBytes(manifesto)) != hash) divergencias.Add("manifesto diverge do indice: " + id);
        }
        foreach (var dir in Directory.Exists(execucoes) ? Directory.EnumerateDirectories(execucoes) : [])
        {
            var id = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, NomeManifesto))) { divergencias.Add("execucao sem manifesto: " + id); continue; }
            if (!linhas.ContainsKey(id)) divergencias.Add("execucao fora do indice: " + id);
            divergencias.AddRange(Verificar(dir).Divergencias.Select(d => id + ": " + d));
        }
        return divergencias;
    }

    internal static string Sha256(byte[] conteudo) => Convert.ToHexStringLower(SHA256.HashData(conteudo));

    private static string Somas(ManifestoDeExecucao147 manifesto) => string.Concat(manifesto.Arquivos.Select(x => $"{x.Sha256}  {x.Nome}\n"));

    // Criado já com 0600: nunca existe, nem por um instante, com as permissões do umask.
    private static void EscreverAtomico(string caminho, byte[] conteudo)
    {
        var parcial = caminho + ".parcial";
        var opcoes = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) opcoes.UnixCreateMode = Arquivo0600;
        using (var fluxo = new FileStream(parcial, opcoes))
        {
            fluxo.Write(conteudo);
            fluxo.Flush(true);
        }
        File.Move(parcial, caminho, overwrite: false);
    }

    private static void CriarPrivado(string diretorio)
    {
        if (new DirectoryInfo(diretorio).LinkTarget is not null) throw new IOException("Diretório de evidências não pode ser link simbólico: " + diretorio);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(diretorio);
        else Directory.CreateDirectory(diretorio, Diretorio0700);
        Restringir(diretorio, Diretorio0700);
    }

    private static void Restringir(string caminho, UnixFileMode modo)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(caminho, modo);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,79}$")]
    private static partial Regex NomeValido();

    [GeneratedRegex("^[0-9]{8}T[0-9]{6}Z-[a-z0-9][a-z0-9.-]{0,79}-r[0-9]+-[0-9a-f]{12}$")]
    private static partial Regex IdValido();
}

// Onde as evidências reais da #147 podem viver: persistente, fora de diretórios temporários e do Worker.
internal static partial class RaizDeEvidencias147
{
    private static readonly string[] SistemasVolateis = ["tmpfs", "ramfs", "devtmpfs"];

    internal static string Padrao()
    {
        var configurada = Environment.GetEnvironmentVariable("DANTE_147_EVIDENCIAS");
        if (!string.IsNullOrWhiteSpace(configurada)) return configurada;
        var dados = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dados))
            dados = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dados, "dante-tests", "issue-147");
    }

    internal static string? LerMounts() => File.Exists("/proc/mounts") ? File.ReadAllText("/proc/mounts") : null;

    // Recusa antes de qualquer chamada ao agente, sem fallback silencioso. Vale para o caminho escrito e para o caminho
    // real, com links simbólicos resolvidos (um link em ~/.local/share apontando para /tmp é recusado).
    // Retorna o caminho real: a execução usa esse, não o link.
    internal static string Validar(string raiz, string? mounts = null)
    {
        var escrito = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raiz));
        var real = CaminhoReal(escrito);
        mounts ??= LerMounts();
        foreach (var caminho in new[] { escrito, real }.Distinct(StringComparer.Ordinal))
        {
            foreach (var p in Proibidos())
                if (Dentro(caminho, p)) throw new InvalidOperationException($"Raiz de evidências da #147 não pode ficar em {p}: {caminho}");
            if (mounts is not null && TipoDoSistemaDeArquivos(caminho, mounts) is { } tipo && SistemasVolateis.Contains(tipo))
                throw new InvalidOperationException($"Raiz de evidências da #147 está em sistema volátil ({tipo}): {caminho}");
        }
        return real;
    }

    private static IEnumerable<string> Proibidos()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] fixos = ["/tmp", "/var/tmp", "/dev/shm", "/run", Path.Combine(home, ".local", "share", "dante"), Path.Combine(home, ".config", "dante")];
        // O temporário do processo e seu caminho real (TMPDIR pode ser link).
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        return fixos.SelectMany(x => new[] { x, CaminhoReal(x) }).Append(temp).Append(CaminhoReal(temp)).Distinct(StringComparer.Ordinal);
    }

    // realpath: resolve links componente a componente; o sufixo que ainda não existe é mantido como escrito.
    internal static string CaminhoReal(string caminho, int profundidade = 0)
    {
        if (profundidade > 40) throw new IOException("Links simbólicos em excesso: " + caminho);
        var completo = Path.GetFullPath(caminho);
        var raiz = Path.GetPathRoot(completo) ?? "/";
        var atual = raiz;
        var partes = completo[raiz.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < partes.Length; i++)
        {
            var proximo = Path.Combine(atual, partes[i]);
            FileSystemInfo info = Directory.Exists(proximo) ? new DirectoryInfo(proximo) : new FileInfo(proximo);
            if (info.LinkTarget is { } alvo)
            {
                var resolvido = Path.IsPathRooted(alvo) ? alvo : Path.Combine(atual, alvo);
                var resto = string.Join(Path.DirectorySeparatorChar, partes[(i + 1)..]);
                return CaminhoReal(resto.Length == 0 ? resolvido : Path.Combine(resolvido, resto), profundidade + 1);
            }
            atual = proximo;
        }
        return Path.TrimEndingDirectorySeparator(atual);
    }

    // Ponto de montagem mais longo que contém o caminho (formato de /proc/mounts, com escapes octais como \040).
    internal static string? TipoDoSistemaDeArquivos(string caminho, string? mounts) =>
        mounts?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' '))
            .Where(c => c.Length >= 3)
            .Select(c => (Ponto: Octal().Replace(c[1], m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString()), Tipo: c[2]))
            .Where(m => Dentro(caminho, m.Ponto))
            .OrderByDescending(m => m.Ponto.Length)
            .Select(m => m.Tipo)
            .FirstOrDefault();

    private static bool Dentro(string caminho, string pai)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pai));
        if (p == "/" || p.Length == 0) return true;
        return caminho == p || caminho.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // Commit do checkout que compila o harness e se a árvore estava limpa; indisponível fora de um repositório Git.
    internal static (string? Commit, bool? Limpa) EstadoGit()
    {
        var commit = Git("rev-parse", "HEAD")?.Trim();
        var status = Git("status", "--porcelain", "--untracked-files=normal");
        return (commit is not null && Hex40().IsMatch(commit) ? commit : null, status is null ? null : status.Length == 0);
    }

    private static string? Git(params string[] argumentos)
    {
        try
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in argumentos) info.ArgumentList.Add(a);
            using var git = Process.Start(info)!;
            var saida = git.StandardOutput.ReadToEnd();
            git.WaitForExit(10_000);
            return git.ExitCode == 0 ? saida : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex Octal();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Hex40();
}

// O pacote exportado é exatamente o injetado: o texto enviado ao agente começa pelo TextoParaInjecao, byte a byte.
internal static class PacoteInjetado147
{
    internal static bool Confere(string textoEnviado, string textoParaInjecao) =>
        textoParaInjecao.Length > 0 && textoEnviado.StartsWith(textoParaInjecao, StringComparison.Ordinal);
}
