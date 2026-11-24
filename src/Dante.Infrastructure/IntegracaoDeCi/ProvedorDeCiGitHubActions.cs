using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Dante.Application.IntegracaoDeCi;
using Dante.Infrastructure.Contextos;

namespace Dante.Infrastructure.IntegracaoDeCi;

// Primeiro provedor da porta de CI (#237, AD-76): GitHub Actions pela API REST, somente leitura. O D.A.N.T.E. observa
// o CI que já roda no GitHub (hosted ou self-hosted: a API e a experiência são as mesmas) e nunca o dirige; não há
// método que dispare, reexecute, cancele, comente ou escreva checks.
//
// Toda chamada passa pela mesma porta de entrada, sem rede até o fim:
// 1. a conexão está configurada no host, é exatamente a configurada e libera o alias (ConexoesDeCiAutorizadas);
// 2. o alias está no cadastro (AD-07) com o mesmo dono/repositorio consultado;
// 3. a credencial é lida do binding do host (AD-10) só então, e só vai no cabeçalho da API.
// Falta de qualquer uma responde NaoConfigurada ou NaoAutorizada. Respostas são conferidas contra a identidade pedida
// (repositório, execução, tentativa, SHA) e o que não confere é recusado inteiro como Inconsistente.
internal sealed class ProvedorDeCiGitHubActions(ConexoesDeCiAutorizadas conexoes, RepositoryRegistry cadastro,
    ClienteDaApiDoGitHub api, TimeProvider relogio) : IProvedorDeCi
{
    // Resposta JSON maior que isso não é uma página de runs ou jobs: é recusada sem ser lida até o fim.
    internal const int TamanhoMaximoDoJson = 8 * 1024 * 1024;
    // Log baixado inteiro além disso não cabe no tempo limite de forma confiável; responde Indisponivel.
    internal const long TamanhoMaximoDoLogBaixado = 64L * 1024 * 1024;
    private const int MaximoNoCache = 1024;

    private readonly PoliticaDeConsultaDeCi politica = api.Politica;
    // Cache da política: tentativa final não muda e não expira; não final vale por ValidadeDoCacheNaoFinal. A chave é a
    // identidade inteira (alias, conexão, repositório, execução, tentativa, SHA): conexões nunca compartilham entrada.
    private readonly ConcurrentDictionary<IdentidadeDaExecucaoDeCi, (ExecucaoDeCiDto Execucao, DateTimeOffset? Expira)> cache = new();

    public async Task<ResultadoDaConsultaDeCi> ConsultarAsync(ConsultaDeCi consulta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        var (credencial, caminho) = Autorizar(consulta.Repositorio);

        var (runs, completo) = await ListarAsync($"{caminho}/actions/runs?head_sha={consulta.ShaDoHead}", "workflow_runs",
            credencial, cancellationToken);
        var execucoes = new List<ExecucaoDeCiDto>();
        foreach (var run in runs.DistinctBy(r => (MapeamentoDoGitHubActions.Long(r, "id"), MapeamentoDoGitHubActions.Long(r, "run_attempt"))))
        {
            var execucao = MapeamentoDoGitHubActions.Execucao(consulta.Repositorio, run);
            // Na listagem o estado do run é fresco: do cache só serve a tentativa já final, que não muda mais.
            if (cache.TryGetValue(execucao.Identidade, out var guardada) && guardada.Expira is null)
            {
                execucoes.Add(guardada.Execucao);
                continue;
            }

            var (comJobs, jobsCompletos) = await ComJobsAsync(execucao, caminho, credencial, cancellationToken);
            completo &= jobsCompletos;
            execucoes.Add(comJobs);
        }

        var possuiWorkflows = execucoes.Count > 0 || await PossuiWorkflowsAsync(caminho, credencial, cancellationToken);
        return new ResultadoDaConsultaDeCi
        {
            PossuiWorkflowsConfigurados = possuiWorkflows,
            Completo = completo,
            ObtidoEm = relogio.GetUtcNow(),
            Execucoes = execucoes
        };
    }

    public async Task<ExecucaoDeCiDto> ObterExecucaoAsync(IdentidadeDaExecucaoDeCi execucao,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execucao);
        var (credencial, caminho) = Autorizar(execucao.Repositorio);
        if (cache.TryGetValue(execucao, out var guardada) && (guardada.Expira is null || guardada.Expira > relogio.GetUtcNow()))
            return guardada.Execucao;

        var run = await ObterJsonAsync($"{caminho}/actions/runs/{execucao.IdDaExecucao}/attempts/{execucao.Tentativa}",
            credencial, cancellationToken);
        var observada = MapeamentoDoGitHubActions.Execucao(execucao.Repositorio, run);
        if (observada.Identidade != execucao)
            throw MapeamentoDoGitHubActions.Inconsistente(
                "O provedor de CI devolveu outra execução, tentativa ou commit; o resultado foi descartado.");

        var (comJobs, completos) = await ComJobsAsync(observada, caminho, credencial, cancellationToken);
        // Sem a lista inteira de jobs a tentativa não é descrita: devolver parte esconderia um job com falha.
        return completos ? comJobs
            : throw new FalhaNaConsultaDeCiException(MotivoDaFalhaNaConsultaDeCi.Indisponivel,
                "A execução tem mais jobs do que o limite de leitura; o resultado ficou incompleto.");
    }

    public async Task<LogDeJobDeCiDto> ObterLogDoJobAsync(IdentidadeDoJobDeCi job, int limiteEmBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (limiteEmBytes is < 1024 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(limiteEmBytes), "Limite do trecho entre 1 KiB e 1 MiB.");
        var (credencial, caminho) = Autorizar(job.Execucao.Repositorio);

        // O job tem de ser da tentativa pedida: nunca se lê o log de outro job por um ID trocado.
        var (codigoDoJob, dadosDoJob) = await TentarAsync(() => api.ObterAsync(Endereco($"{caminho}/actions/jobs/{job.IdDoJob}"),
            credencial, async (resposta, token) => ((HttpStatusCode?)resposta.StatusCode,
                resposta.IsSuccessStatusCode ? await LerJsonAsync(resposta, token) : default), cancellationToken));
        if (codigoDoJob is not HttpStatusCode.OK)
            return new LogDeJobDeCiDto(job, codigoDoJob is null ? DisponibilidadeDoLogDeCi.Indisponivel : Disponibilidade(codigoDoJob.Value));
        MapeamentoDoGitHubActions.ExigirDaTentativa(job.Execucao, dadosDoJob);
        if (MapeamentoDoGitHubActions.Long(dadosDoJob, "id") != job.IdDoJob)
            throw MapeamentoDoGitHubActions.Inconsistente("O provedor de CI devolveu outro job; o resultado foi descartado.");
        var estado = MapeamentoDoGitHubActions.EstadoDoJob(MapeamentoDoGitHubActions.TextoOpcional(dadosDoJob, "status"),
            MapeamentoDoGitHubActions.TextoOpcional(dadosDoJob, "conclusion"));
        if (!TransicoesDaExecucaoDeCi.Final(estado))
            return new LogDeJobDeCiDto(job, DisponibilidadeDoLogDeCi.AindaEmAndamento);

        // A API responde com redirect para um endereço assinado e temporário; ele é seguido sem a credencial.
        var janela = limiteEmBytes * 4;
        var (codigo, destino, log) = await TentarAsync(() => api.ObterAsync(Endereco($"{caminho}/actions/jobs/{job.IdDoJob}/logs"),
            credencial, (resposta, token) => LerLogOuRedirectAsync(resposta, janela, token), cancellationToken));
        if (destino is not null)
            (codigo, _, log) = await TentarAsync(() => api.ObterAsync(destino, null,
                (resposta, token) => LerLogOuRedirectAsync(resposta, janela, token, seguirRedirect: false), cancellationToken));

        if (codigo is null) return new LogDeJobDeCiDto(job, DisponibilidadeDoLogDeCi.Indisponivel);
        if (codigo != HttpStatusCode.OK || log is null) return new LogDeJobDeCiDto(job, Disponibilidade(codigo.Value));
        return new LogDeJobDeCiDto(job, DisponibilidadeDoLogDeCi.Disponivel,
            SanitizacaoDeTextoDeCi.Sanitizar(log.Value.Texto, limiteEmBytes, log.Value.InicioCortado));
    }

    // Portas 1 e 2 do comentário da classe, depois a leitura da credencial. Nada de rede antes daqui.
    private (string Credencial, string Caminho) Autorizar(RepositorioDeCi repositorio)
    {
        ArgumentNullException.ThrowIfNull(repositorio);
        var variavel = conexoes.ExigirAutorizada(repositorio);
        if (cadastro.Get(repositorio.Alias)?.GitHub is not { } cadastrado
            || !string.Equals(cadastrado, repositorio.NomeCompleto, StringComparison.OrdinalIgnoreCase))
            throw new FalhaNaConsultaDeCiException(MotivoDaFalhaNaConsultaDeCi.NaoAutorizada,
                "O repositório não está cadastrado no D.A.N.T.E. com este nome no provedor.");
        var partes = repositorio.NomeCompleto.Split('/');
        return (conexoes.LerCredencial(variavel),
            $"repos/{Uri.EscapeDataString(partes[0])}/{Uri.EscapeDataString(partes[1])}");
    }

    private async Task<(ExecucaoDeCiDto Execucao, bool Completos)> ComJobsAsync(ExecucaoDeCiDto execucao, string caminho,
        string credencial, CancellationToken cancellationToken)
    {
        var identidade = execucao.Identidade;
        var (jobs, completos) = await ListarAsync(
            $"{caminho}/actions/runs/{identidade.IdDaExecucao}/attempts/{identidade.Tentativa}/jobs", "jobs", credencial,
            cancellationToken);
        var comJobs = execucao with
        {
            Jobs = jobs.Select(j => MapeamentoDoGitHubActions.Job(identidade, j)).DistinctBy(j => j.IdDoJob).ToArray()
        };
        if (completos) Guardar(comJobs);
        return (comJobs, completos);
    }

    private void Guardar(ExecucaoDeCiDto execucao)
    {
        if (cache.Count >= MaximoNoCache) cache.Clear();
        cache[execucao.Identidade] = (execucao, TransicoesDaExecucaoDeCi.Final(execucao.Estado)
            ? null
            : relogio.GetUtcNow() + politica.ValidadeDoCacheNaoFinal);
    }

    private async Task<bool> PossuiWorkflowsAsync(string caminho, string credencial, CancellationToken cancellationToken)
    {
        var workflows = await ObterJsonAsync($"{caminho}/actions/workflows?per_page=1", credencial, cancellationToken);
        return MapeamentoDoGitHubActions.Long(workflows, "total_count") > 0;
    }

    // Paginação até o limite da política. Completo só quando a última página foi lida: parar no limite com mais itens
    // anunciados é resultado incompleto, nunca "não há mais".
    private async Task<(IReadOnlyList<JsonElement> Itens, bool Completo)> ListarAsync(string recurso, string campo,
        string credencial, CancellationToken cancellationToken)
    {
        var itens = new List<JsonElement>();
        var separador = recurso.Contains('?') ? '&' : '?';
        for (var pagina = 1; pagina <= politica.PaginasMaximas; pagina++)
        {
            var corpo = await ObterJsonAsync($"{recurso}{separador}per_page={politica.ItensPorPagina}&page={pagina}",
                credencial, cancellationToken);
            var total = MapeamentoDoGitHubActions.Long(corpo, "total_count");
            if (!corpo.TryGetProperty(campo, out var lista) || lista.ValueKind != JsonValueKind.Array)
                throw MapeamentoDoGitHubActions.Inconsistente("Resposta do provedor de CI sem a lista esperada.");
            var daPagina = lista.EnumerateArray().ToArray();
            itens.AddRange(daPagina);
            if (daPagina.Length < politica.ItensPorPagina || itens.Count >= total) return (itens, true);
        }

        return (itens, false);
    }

    private Task<JsonElement> ObterJsonAsync(string recurso, string credencial, CancellationToken cancellationToken) =>
        api.ObterAsync(Endereco(recurso), credencial, async (resposta, token) =>
        {
            if (!resposta.IsSuccessStatusCode) throw FalhaHttp(resposta.StatusCode);
            return await LerJsonAsync(resposta, token);
        }, cancellationToken);

    private static async Task<JsonElement> LerJsonAsync(HttpResponseMessage resposta, CancellationToken cancellationToken)
    {
        if (resposta.Content.Headers.ContentLength > TamanhoMaximoDoJson) throw GrandeDemais();
        await using var fluxo = await resposta.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bloco = new byte[81920];
        int lidos;
        while ((lidos = await fluxo.ReadAsync(bloco, cancellationToken)) > 0)
        {
            if (buffer.Length + lidos > TamanhoMaximoDoJson) throw GrandeDemais();
            buffer.Write(bloco, 0, lidos);
        }

        try
        {
            using var documento = JsonDocument.Parse(buffer.ToArray());
            return documento.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw MapeamentoDoGitHubActions.Inconsistente("O provedor de CI devolveu uma resposta que não é JSON válido.");
        }

        static FalhaNaConsultaDeCiException GrandeDemais() =>
            MapeamentoDoGitHubActions.Inconsistente("O provedor de CI devolveu uma resposta maior que o limite de leitura.");
    }

    // Lê só o final do log: a janela é quatro vezes o limite do trecho, como o corte prévio da sanitização. Redirect
    // (3xx com Location https) é devolvido para ser seguido sem credencial; demais códigos voltam como estão.
    private static async Task<(HttpStatusCode? Codigo, Uri? Destino, (string Texto, bool InicioCortado)? Log)> LerLogOuRedirectAsync(
        HttpResponseMessage resposta, int janela, CancellationToken cancellationToken, bool seguirRedirect = true)
    {
        var codigo = (int)resposta.StatusCode;
        if (codigo is >= 300 and < 400)
            return seguirRedirect && resposta.Headers.Location is { IsAbsoluteUri: true } destino && destino.Scheme == Uri.UriSchemeHttps
                ? (resposta.StatusCode, destino, null)
                : (HttpStatusCode.BadGateway, null, null);
        if (resposta.StatusCode != HttpStatusCode.OK) return (resposta.StatusCode, null, null);
        if (resposta.Content.Headers.ContentLength > TamanhoMaximoDoLogBaixado) return (null, null, null);

        await using var fluxo = await resposta.Content.ReadAsStreamAsync(cancellationToken);
        using var acumulado = new MemoryStream();
        var bloco = new byte[81920];
        long total = 0;
        int lidos;
        while ((lidos = await fluxo.ReadAsync(bloco, cancellationToken)) > 0)
        {
            total += lidos;
            if (total > TamanhoMaximoDoLogBaixado) return (null, null, null);
            acumulado.Write(bloco, 0, lidos);
            if (acumulado.Length <= janela * 2L) continue;
            var final = acumulado.ToArray()[^janela..];
            acumulado.SetLength(0);
            acumulado.Write(final);
        }

        var bytes = acumulado.ToArray();
        if (bytes.Length > janela) bytes = bytes[^janela..];
        return (HttpStatusCode.OK, null, (Encoding.UTF8.GetString(bytes), total > bytes.Length));
    }

    // Falha transitória esgotada ou limite de taxa durante a leitura do log não esconde nada: o log fica Indisponivel
    // (codigo nulo) e o estado do job continua sendo o que a consulta da execução observou. Inconsistência continua
    // sendo falha da consulta.
    private static async Task<T> TentarAsync<T>(Func<Task<T>> ler) where T : struct
    {
        try
        {
            return await ler();
        }
        catch (FalhaNaConsultaDeCiException ex) when (ex.Motivo is MotivoDaFalhaNaConsultaDeCi.Indisponivel
                                                          or MotivoDaFalhaNaConsultaDeCi.LimiteDeTaxa)
        {
            return default;
        }
    }

    private static DisponibilidadeDoLogDeCi Disponibilidade(HttpStatusCode codigo) => codigo switch
    {
        HttpStatusCode.NotFound => DisponibilidadeDoLogDeCi.NaoEncontrado,
        HttpStatusCode.Gone => DisponibilidadeDoLogDeCi.Expirado,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DisponibilidadeDoLogDeCi.SemPermissao,
        _ => DisponibilidadeDoLogDeCi.Indisponivel
    };

    private static FalhaNaConsultaDeCiException FalhaHttp(HttpStatusCode codigo) => codigo switch
    {
        HttpStatusCode.Unauthorized => new(MotivoDaFalhaNaConsultaDeCi.NaoAutorizada,
            "O provedor de CI recusou a credencial da conexão."),
        HttpStatusCode.Forbidden => new(MotivoDaFalhaNaConsultaDeCi.SemPermissao,
            "A credencial da conexão não tem permissão para ler o CI deste repositório."),
        HttpStatusCode.NotFound => new(MotivoDaFalhaNaConsultaDeCi.NaoEncontrada,
            "O provedor de CI não encontrou o repositório ou a execução com esta credencial."),
        >= (HttpStatusCode)300 and < (HttpStatusCode)400 => MapeamentoDoGitHubActions.Inconsistente(
            "O repositório mudou de nome ou de dono no provedor; atualize o cadastro."),
        _ => new(MotivoDaFalhaNaConsultaDeCi.Indisponivel, "O provedor de CI recusou a consulta.")
    };

    private static Uri Endereco(string recurso) => new(ClienteDaApiDoGitHub.Base, recurso);
}
