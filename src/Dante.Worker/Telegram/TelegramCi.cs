using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dante.Application.ConversaDoBrain;
using Dante.Application.IntegracaoDeCi;

namespace Dante.Worker.Telegram;

// Adapter de entrada do andamento do CI de um PR (#238, AD-77): reconhece /ci e a pergunta natural numa gramática
// fechada e determinística (como as intenções do Brain), chama o caso de uso da Application num escopo próprio e
// formata a resposta. Nenhuma regra de correlação vive aqui: repositório, head SHA, descarte de commits antigos e
// estados são da Application. A consulta é sob demanda; não há acompanhamento periódico.
public sealed class TelegramCi(IServiceScopeFactory escopos, ILogger<TelegramCi> logger)
{
    internal const string Uso = "Uso: /ci [@alias] <número do PR>, por exemplo /ci @exemplo 12. Também dá para perguntar: " +
        "como está o CI do PR 12 do @exemplo?";
    internal const string Indisponivel = "Consulta de CI não está disponível neste Worker.";
    // A consulta roda no fluxo das mensagens: um provedor lento não segura o bot além disso.
    internal static readonly TimeSpan TempoMaximo = TimeSpan.FromSeconds(45);
    private const int MaximoDeWorkflows = 10;
    private const int MaximoDeJobs = 5;

    // Pergunta natural, já sem acentos e em minúsculas. Exige "CI" (ou checks/pipeline) e o número do PR; o alias só vale
    // quando dito explicitamente. Qualquer outra frase segue para a conversa com o agente.
    private static readonly Regex Natural = new(
        @"^(?:e\s+)?(?:(?:como\s+(?:esta|anda|ficou|vai)|qual\s+(?:e\s+)?(?:o\s+)?(?:status|estado|situacao|resultado)\s+d[oa]|" +
        @"(?:status|estado|situacao|resultado)\s+d[oa])\s+)?(?:o\s+)?(?:ci|checks?|pipeline)\s+(?:d[oa]|n[oa])\s+" +
        @"(?:pr|pull\s+request)\s*#?(?<n>\d{1,9})(?:\s+(?:d[oa]|n[oa]|de|em)\s+(?<alias>@[a-z][a-z0-9_]*))?" +
        @"(?:\s+(?:ja\s+)?(?:passou|terminou|falhou|rodou))?\s*[?!.]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    // Numero nulo: era /ci, mas sem um número de PR válido (responde o uso).
    internal sealed record Pedido(int? Numero, string? Alias);

    // Null quando o texto não é um pedido de CI.
    internal static Pedido? Interpretar(string texto)
    {
        texto = texto.Trim();
        var separador = texto.IndexOfAny([' ', '\t', '\r', '\n']);
        var comando = separador < 0 ? texto : texto[..separador];
        if (comando.Equals("/ci", StringComparison.OrdinalIgnoreCase))
        {
            string? alias = null;
            int? numero = null;
            foreach (var parte in (separador < 0 ? "" : texto[(separador + 1)..])
                         .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (parte.StartsWith('@') && alias is null) alias = parte;
                else if (numero is null && Numero(parte.TrimStart('#')) is { } lido) numero = lido;
                else return new Pedido(null, null);
            }
            return numero is null ? new Pedido(null, null) : new Pedido(numero, alias);
        }
        if (texto.StartsWith('/') || texto.Length > 200) return null;

        var normalizado = Regex.Replace(ResolvedorDeIntencaoDoBrain.Normalizar(texto), @"\s+", " ",
            RegexOptions.None, TimeSpan.FromMilliseconds(100));
        var encontrado = Natural.Match(normalizado);
        return encontrado.Success && Numero(encontrado.Groups["n"].Value) is { } n
            ? new Pedido(n, encontrado.Groups["alias"].Success ? encontrado.Groups["alias"].Value : null)
            : null;
    }

    internal async Task<string> AtenderAsync(long idDoUsuario, string? aliasDaSessao, Pedido pedido,
        CancellationToken cancellationToken)
    {
        if (pedido.Numero is not { } numero) return Uso;
        using var prazo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        prazo.CancelAfter(TempoMaximo);
        try
        {
            using var escopo = escopos.CreateScope();
            var situacao = await escopo.ServiceProvider.GetRequiredService<IAcompanhamentoDeCiAppService>()
                .ConsultarPullRequestAsync(new PedidoDeCiDoPullRequestDto(idDoUsuario, numero, pedido.Alias, aliasDaSessao),
                    prazo.Token);
            return Formatar(situacao);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"Não consegui consultar o CI do PR #{numero} a tempo. Isso não quer dizer que o CI falhou; tente de " +
                   "novo em instantes.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Só o tipo: a mensagem de uma falha inesperada pode trazer detalhes do provedor.
            logger.LogWarning("Falha na consulta de CI ({ErrorType}).", exception.GetType().Name);
            return $"Consulta do CI do PR #{numero} indisponível agora. Isso não quer dizer que o CI falhou.";
        }
    }

    internal static string Formatar(SituacaoDoCiDoPullRequestDto situacao)
    {
        var numero = situacao.NumeroDoPr;
        var alias = situacao.Alias ?? "o repositório";
        if (situacao.ProblemaDoRepositorio is { } problema)
            return problema switch
            {
                ProblemaDoRepositorioDeCi.AliasInvalido => $"Alias inválido: {situacao.Alias}. Use @nome, como em /repos.",
                ProblemaDoRepositorioDeCi.NaoCadastrado => $"Repositório {situacao.Alias} não cadastrado. Veja /repos.",
                ProblemaDoRepositorioDeCi.AtivoForaDoCadastro => $"O repositório ativo {situacao.Alias} não está mais " +
                    $"cadastrado. Use /use @alias ou diga o repositório: /ci @alias {numero}.",
                _ => $"De qual repositório é o PR #{numero}? Diga o alias, por exemplo /ci @alias {numero}, ou selecione um " +
                     "com /use @alias."
            };

        if (situacao.FalhaDoPullRequest is { } falha)
            return falha switch
            {
                MotivoDaFalhaNaConsultaDeCi.NaoConfigurada => $"O CI de {alias} não está configurado neste D.A.N.T.E.: " +
                    "falta uma conexão de CI no host que libere esse repositório, ou o dono/repositório no cadastro " +
                    $"(/repo show {alias}). Repositórios vinculados não herdam a conexão do principal.",
                MotivoDaFalhaNaConsultaDeCi.NaoAutorizada => $"A conexão de CI do host não pode consultar {alias} (repositório " +
                    "fora da conexão, cadastro divergente ou credencial recusada). Confira a configuração do host.",
                MotivoDaFalhaNaConsultaDeCi.SemPermissao => $"A conexão de CI não tem permissão para ler o PR #{numero} de " +
                    $"{alias}. Não sei o resultado do CI.",
                MotivoDaFalhaNaConsultaDeCi.NaoEncontrada => $"Não encontrei o PR #{numero} em {alias} (ou a conexão de CI " +
                    "não enxerga esse repositório).",
                MotivoDaFalhaNaConsultaDeCi.LimiteDeTaxa => $"O provedor limitou as consultas agora; tente de novo" +
                    $"{Quando(situacao.RepetirApos)}. Não sei o resultado do CI do PR #{numero}.",
                MotivoDaFalhaNaConsultaDeCi.Inconsistente => $"A resposta sobre o PR #{numero} não confere com {alias} " +
                    "(outro repositório ou dados inválidos); nada dela foi usado. Não sei o resultado do CI.",
                _ => $"Não consegui ler o PR #{numero} de {alias} agora: o provedor não respondeu. Não sei o resultado do CI; " +
                     "tente de novo em instantes."
            };

        var pr = situacao.PullRequest!;
        var check = situacao.Check!;
        var texto = new StringBuilder();
        texto.Append($"PR #{numero} de {alias}: {pr.Titulo}");
        var marcas = new List<string>();
        if (pr.Estado == EstadoDoPullRequestDeCi.Mesclado) marcas.Add("mesclado");
        else if (pr.Estado == EstadoDoPullRequestDeCi.Fechado) marcas.Add("fechado");
        if (pr.Rascunho) marcas.Add("rascunho");
        if (pr.DeFork) marcas.Add("de fork");
        if (marcas.Count > 0) texto.Append($" ({string.Join(", ", marcas)})");
        texto.Append($"\nCommit atual: {pr.ShaDoHead[..7]}");
        texto.Append($"\nCI: {Resumo(check, situacao.RepetirApos)}");

        foreach (var execucao in check.Execucoes.Take(MaximoDeWorkflows))
            texto.Append('\n').Append(Linha(execucao));
        if (check.Execucoes.Count > MaximoDeWorkflows)
            texto.Append($"\n… e mais {check.Execucoes.Count - MaximoDeWorkflows} workflow(s).");
        if (check.Descartadas > 0)
            texto.Append("\nExecuções de commits anteriores, de outros PRs e tentativas substituídas não contam para este " +
                         "resultado.");
        if (check.SemAssociacaoAoPr > 0 && check.Execucoes.Count > 0)
            texto.Append(check.SemAssociacaoAoPr == 1
                ? "\n1 execução deste commit não aparece ligada a este PR no provedor e não foi considerada."
                : $"\n{check.SemAssociacaoAoPr} execuções deste commit não aparecem ligadas a este PR no provedor e não " +
                  "foram consideradas.");
        if (pr.DeFork && check.Estado == EstadoDoCheckDeCi.NaFila)
            texto.Append("\nEm PR de fork, a execução pode estar esperando a aprovação de um mantenedor para rodar.");
        if (check.Estado == EstadoDoCheckDeCi.Sucesso)
            texto.Append("\nIsso não diz se o PR pode ser mesclado: proteção da branch, checks obrigatórios e revisões " +
                         "não foram avaliados.");
        texto.Append($"\nPR: {pr.UrlOficial}");
        return texto.ToString();
    }

    private static string Resumo(CheckAgregadoDeCiDto check, TimeSpan? repetirApos)
    {
        var relevantes = check.Execucoes.Where(e => e.Estado != EstadoDaExecucao.Ignorada).ToArray();
        var concluidos = relevantes.Count(e => TransicoesDaExecucaoDeCi.Final(e.Estado));
        var andamento = relevantes.Length > 0 ? $" ({concluidos} de {relevantes.Length} workflow(s) concluído(s))" : "";
        var parcial = check.Completo ? "" : " O resultado veio incompleto (muitas execuções).";
        return check.Estado switch
        {
            EstadoDoCheckDeCi.Sucesso => $"tudo passou ({relevantes.Length} workflow(s)).",
            EstadoDoCheckDeCi.Falha => $"falhou.{parcial}",
            EstadoDoCheckDeCi.Cancelado => "cancelado.",
            EstadoDoCheckDeCi.Expirado => "expirou (tempo limite do CI).",
            EstadoDoCheckDeCi.EmAndamento => $"em andamento{andamento}.",
            EstadoDoCheckDeCi.AguardandoRunner => $"aguardando runner{andamento}.",
            EstadoDoCheckDeCi.NaFila => $"na fila{andamento}.",
            EstadoDoCheckDeCi.SemExecucao => "ainda não há execução de CI para este commit.",
            EstadoDoCheckDeCi.SemWorkflowConfigurado => "o repositório não tem workflow de CI configurado.",
            EstadoDoCheckDeCi.SemPermissao => "sem permissão para ler o CI deste repositório. Isso não quer dizer que o " +
                                              "CI falhou.",
            _ => check.MotivoDaIndisponibilidade == MotivoDaFalhaNaConsultaDeCi.LimiteDeTaxa
                ? $"o provedor limitou as consultas; tente de novo{Quando(repetirApos)}. Isso não quer dizer que o CI falhou."
                : check.Execucoes.Count == 0 && check.SemAssociacaoAoPr > 0
                    ? "não consigo afirmar o resultado: há execução de CI deste commit, mas o provedor não a liga a este " +
                      "PR, então ela não foi considerada. Isso não quer dizer que o CI falhou."
                : check.Execucoes.Count > 0 && !check.Completo
                    ? "o resultado veio incompleto (muitas execuções), então não afirmo sucesso. Isso não quer dizer " +
                      "que o CI falhou."
                    : "não consegui saber o resultado agora. Isso não quer dizer que o CI falhou."
        };
    }

    // Um workflow: estado, tentativa (reexecução), o link da execução e, abaixo, o que falhou ou o que falta.
    private static string Linha(ExecucaoDeCiDto execucao)
    {
        var tentativa = execucao.Identidade.Tentativa > 1 ? $", tentativa {execucao.Identidade.Tentativa}" : "";
        var linha = new StringBuilder($"• {execucao.NomeDoWorkflow}: {Estado(execucao.Estado)}{tentativa} — {execucao.UrlOficial}");
        var falhos = execucao.Jobs.Where(j => j.Estado is EstadoDaExecucao.Falha or EstadoDaExecucao.Expirada).ToArray();
        foreach (var job in falhos.Take(MaximoDeJobs))
        {
            var etapa = job.Etapas.FirstOrDefault(e => e.Estado is EstadoDaExecucao.Falha or EstadoDaExecucao.Expirada);
            linha.Append($"\n   {job.Nome} {Estado(job.Estado)}" + (etapa is null ? "" : $" na etapa {etapa.Nome}"));
        }
        if (falhos.Length > MaximoDeJobs) linha.Append($"\n   … e mais {falhos.Length - MaximoDeJobs} job(s) com falha");

        var pendentes = execucao.Jobs.Where(j => !TransicoesDaExecucaoDeCi.Final(j.Estado)).ToArray();
        if (pendentes.Length > 0)
            linha.Append("\n   pendente: " + string.Join(", ", pendentes.Take(MaximoDeJobs)
                .Select(j => $"{j.Nome} ({Estado(j.Estado)})")) +
                (pendentes.Length > MaximoDeJobs ? $" e mais {pendentes.Length - MaximoDeJobs}" : ""));
        return linha.ToString();
    }

    private static string Estado(EstadoDaExecucao estado) => estado switch
    {
        EstadoDaExecucao.NaFila => "na fila",
        EstadoDaExecucao.AguardandoRunner => "aguardando runner",
        EstadoDaExecucao.EmAndamento => "em andamento",
        EstadoDaExecucao.Sucesso => "passou",
        EstadoDaExecucao.Falha => "falhou",
        EstadoDaExecucao.Cancelada => "cancelado",
        EstadoDaExecucao.Expirada => "expirou",
        _ => "ignorado"
    };

    private static string Quando(TimeSpan? repetirApos) => repetirApos switch
    {
        null => " em alguns minutos",
        { TotalMinutes: >= 1 } espera => $" em cerca de {Math.Ceiling(espera.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min",
        { } espera => $" em {Math.Max(1, Math.Ceiling(espera.TotalSeconds)).ToString(CultureInfo.InvariantCulture)} s"
    };

    private static int? Numero(string texto) =>
        int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var numero) && numero > 0 ? numero : null;
}
