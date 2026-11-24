using System.Globalization;
using System.Text.Json;
using Dante.Application.IntegracaoDeCi;

namespace Dante.Infrastructure.IntegracaoDeCi;

// Tradução das respostas do GitHub Actions para os contratos neutros (#237, AD-76). Toda resposta é dado não confiável:
// campo ausente, de outro tipo, ID não positivo, SHA abreviado, URL fora do github.com ou status/conclusão sem
// mapeamento conhecido viram FalhaNaConsultaDeCiException(Inconsistente), nunca um estado aproximado. Nomes vindos do
// workflow passam pela SanitizacaoDeTextoDeCi antes de virar texto exibível.
//
//   status                                   execução / etapa     job
//   queued                                   NaFila               AguardandoRunner (o job espera um runner)
//   requested, pending, waiting              NaFila               NaFila
//   in_progress                              EmAndamento          EmAndamento
//   completed + success                      Sucesso
//   completed + failure, startup_failure     Falha
//   completed + cancelled                    Cancelada
//   completed + timed_out, stale             Expirada
//   completed + skipped, neutral             Ignorada (neutral não afirma sucesso nem falha: não conta)
//   completed + action_required              NaFila (aguarda aprovação humana para rodar; ainda não executou)
internal static class MapeamentoDoGitHubActions
{
    private const string HostOficial = "github.com";
    private const int TamanhoMaximoDoNome = 200;

    internal static EstadoDaExecucao EstadoDaExecucaoOuEtapa(string? status, string? conclusao) =>
        status switch
        {
            "queued" or "requested" or "pending" or "waiting" => EstadoDaExecucao.NaFila,
            "in_progress" => EstadoDaExecucao.EmAndamento,
            "completed" => Conclusao(conclusao),
            _ => throw Inconsistente("O provedor de CI devolveu um status desconhecido.")
        };

    internal static EstadoDaExecucao EstadoDoJob(string? status, string? conclusao) =>
        status == "queued" ? EstadoDaExecucao.AguardandoRunner : EstadoDaExecucaoOuEtapa(status, conclusao);

    private static EstadoDaExecucao Conclusao(string? conclusao) => conclusao switch
    {
        "success" => EstadoDaExecucao.Sucesso,
        "failure" or "startup_failure" => EstadoDaExecucao.Falha,
        "cancelled" => EstadoDaExecucao.Cancelada,
        "timed_out" or "stale" => EstadoDaExecucao.Expirada,
        "skipped" or "neutral" => EstadoDaExecucao.Ignorada,
        "action_required" => EstadoDaExecucao.NaFila,
        _ => throw Inconsistente("O provedor de CI devolveu uma conclusão desconhecida.")
    };

    // Execução (workflow run) sem jobs. O repositório da resposta tem de ser o esperado: mesmo nome e, quando já
    // conhecido, mesmo ID. O SHA é o que o provedor diz que a execução testou, nunca o da consulta.
    internal static ExecucaoDeCiDto Execucao(RepositorioDeCi esperado, JsonElement run) => Traduzir(() =>
    {
        var repositorio = Objeto(run, "repository");
        var idDoRepositorio = Long(repositorio, "id");
        if (!string.Equals(Texto(repositorio, "full_name"), esperado.NomeCompleto, StringComparison.OrdinalIgnoreCase)
            || esperado.IdNoProvedor is { } id && id != idDoRepositorio)
            throw Inconsistente("O provedor de CI devolveu execução de outro repositório; o resultado foi descartado.");

        var confirmado = new RepositorioDeCi(esperado.Alias, esperado.NomeCompleto, idDoRepositorio, esperado.Conexao);
        var idDaExecucao = Long(run, "id");
        return new ExecucaoDeCiDto
        {
            Identidade = new IdentidadeDaExecucaoDeCi(confirmado, Long(run, "workflow_id"), idDaExecucao,
                Int(run, "run_attempt"), Texto(run, "head_sha")),
            NomeDoWorkflow = Nome(TextoOpcional(run, "name"), "workflow"),
            Gatilho = Nome(TextoOpcional(run, "event"), "desconhecido"),
            Estado = EstadoDaExecucaoOuEtapa(TextoOpcional(run, "status"), TextoOpcional(run, "conclusion")),
            UrlOficial = UrlOficial(run, esperado.NomeCompleto),
            CriadaEm = Data(run, "created_at"),
            AtualizadaEm = Data(run, "updated_at"),
            NumerosDePr = run.TryGetProperty("pull_requests", out var prs) && prs.ValueKind == JsonValueKind.Array
                ? prs.EnumerateArray().Select(pr => Int(pr, "number")).Distinct().Order().ToArray()
                : []
        };
    });

    // Job de uma tentativa: precisa ser da mesma execução, da mesma tentativa e do mesmo SHA. Job de outra tentativa
    // nunca entra numa execução (reexecução é outra identidade).
    internal static JobDeCiDto Job(IdentidadeDaExecucaoDeCi execucao, JsonElement job) => Traduzir(() =>
    {
        ExigirDaTentativa(execucao, job);
        return new JobDeCiDto
        {
            IdDoJob = Long(job, "id"),
            Nome = Nome(TextoOpcional(job, "name"), "job"),
            Estado = EstadoDoJob(TextoOpcional(job, "status"), TextoOpcional(job, "conclusion")),
            UrlOficial = UrlOficial(job, execucao.Repositorio.NomeCompleto),
            IniciadoEm = DataOpcional(job, "started_at"),
            ConcluidoEm = DataOpcional(job, "completed_at"),
            Etapas = job.TryGetProperty("steps", out var etapas) && etapas.ValueKind == JsonValueKind.Array
                ? etapas.EnumerateArray().Select(etapa => new EtapaDeJobDeCiDto(Int(etapa, "number"),
                    Nome(TextoOpcional(etapa, "name"), "etapa"),
                    EstadoDaExecucaoOuEtapa(TextoOpcional(etapa, "status"), TextoOpcional(etapa, "conclusion"))))
                    .OrderBy(e => e.Numero).ToArray()
                : []
        };
    });

    internal static void ExigirDaTentativa(IdentidadeDaExecucaoDeCi execucao, JsonElement job) => Traduzir(() =>
    {
        if (Long(job, "run_id") != execucao.IdDaExecucao || Int(job, "run_attempt") != execucao.Tentativa
            || !string.Equals(Texto(job, "head_sha"), execucao.Sha, StringComparison.OrdinalIgnoreCase))
            throw Inconsistente("O provedor de CI devolveu job de outra execução, tentativa ou commit; o resultado foi descartado.");
        return true;
    });

    internal static long Long(JsonElement elemento, string nome) =>
        Campo(elemento, nome) is { ValueKind: JsonValueKind.Number } valor && valor.TryGetInt64(out var numero)
            ? numero
            : throw Inconsistente("Resposta do provedor de CI sem um campo numérico obrigatório.");

    internal static string? TextoOpcional(JsonElement elemento, string nome) =>
        elemento.ValueKind == JsonValueKind.Object && elemento.TryGetProperty(nome, out var valor)
            ? valor.ValueKind switch
            {
                JsonValueKind.String => valor.GetString(),
                JsonValueKind.Null => null,
                _ => throw Inconsistente("Resposta do provedor de CI com campo de texto em formato inesperado.")
            }
            : null;

    internal static FalhaNaConsultaDeCiException Inconsistente(string mensagem) =>
        new(MotivoDaFalhaNaConsultaDeCi.Inconsistente, mensagem);

    // Validações dos contratos (ArgumentException) e acessos a JSON de forma inesperada também são inconsistência do
    // provedor: o que chega à Application ou é um DTO válido ou é uma falha da consulta.
    private static T Traduzir<T>(Func<T> traduzir)
    {
        try
        {
            return traduzir();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw Inconsistente("O provedor de CI devolveu dados que não formam uma execução válida.");
        }
    }

    private static int Int(JsonElement elemento, string nome) =>
        Long(elemento, nome) is var valor and >= int.MinValue and <= int.MaxValue
            ? (int)valor
            : throw Inconsistente("Resposta do provedor de CI com número fora da faixa.");

    private static string Texto(JsonElement elemento, string nome) =>
        TextoOpcional(elemento, nome) ?? throw Inconsistente("Resposta do provedor de CI sem um campo de texto obrigatório.");

    private static JsonElement Objeto(JsonElement elemento, string nome) =>
        Campo(elemento, nome) is { ValueKind: JsonValueKind.Object } valor
            ? valor
            : throw Inconsistente("Resposta do provedor de CI sem um objeto obrigatório.");

    private static JsonElement? Campo(JsonElement elemento, string nome) =>
        elemento.ValueKind == JsonValueKind.Object && elemento.TryGetProperty(nome, out var valor) ? valor : null;

    private static DateTimeOffset Data(JsonElement elemento, string nome) =>
        DataOpcional(elemento, nome) ?? throw Inconsistente("Resposta do provedor de CI sem data obrigatória.");

    private static DateTimeOffset? DataOpcional(JsonElement elemento, string nome) =>
        TextoOpcional(elemento, nome) is not { } texto ? null
        : DateTimeOffset.TryParse(texto, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var data) ? data
        : throw Inconsistente("Resposta do provedor de CI com data inválida.");

    // Só a página da execução ou do job no github.com, dentro do próprio repositório: nunca a URL da API nem de outro
    // repositório.
    private static Uri UrlOficial(JsonElement elemento, string nomeCompleto)
    {
        if (!Uri.TryCreate(TextoOpcional(elemento, "html_url"), UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo) || !url.IsDefaultPort
            || !string.Equals(url.Host, HostOficial, StringComparison.OrdinalIgnoreCase)
            || !url.AbsolutePath.StartsWith($"/{nomeCompleto}/", StringComparison.OrdinalIgnoreCase))
            throw Inconsistente("O provedor de CI devolveu um link que não é a página oficial da execução.");
        return url;
    }

    // Nome de workflow, job, etapa ou evento: escrito por quem controla o repositório. Sem sequências de terminal nem
    // invisíveis, linha de credencial aparente redigida, uma linha só e curta.
    private static string Nome(string? bruto, string padrao)
    {
        var limpo = string.Join(' ', SanitizacaoDeTextoDeCi.Sanitizar(bruto, 1024).Texto
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (limpo.Length == 0) return padrao;
        return limpo.Length > TamanhoMaximoDoNome ? limpo[..TamanhoMaximoDoNome] : limpo;
    }
}
