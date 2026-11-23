using System.Globalization;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Worker.Execucoes;

namespace Dante.Worker.Telegram;

// Adaptador de decisão das ações protegidas (AD-72, #311, PR 2): mostra o pedido com resumo redigido, hash curto e
// botões, e encaminha aprovar/recusar à Application. Nenhuma regra de autorização vive aqui: dono, prazo, hash,
// revogação e decisão única são do IAprovacoesDeAcaoAppService. Os pedidos vêm do ledger, então /acoes os mostra de novo
// depois de um reinício, e um botão antigo continua valendo só se a Application ainda aceitar a decisão. Sem botões
// (transporte sem teclado inline), os comandos textuais equivalentes. Só existe com Orquestracao:Habilitada.
public sealed class TelegramAprovacoesDeAcao(IServiceScopeFactory escopos, ITelegramBotApi telegram,
    ILogger<TelegramAprovacoesDeAcao> logger)
{
    public const string Desabilitado =
        "Aprovações de ação não estão habilitadas neste Worker (dependem do ledger de execuções).";
    public const string Indisponivel = "Aprovações de ação indisponíveis (banco de dados); nada foi decidido. Tente novamente.";
    internal const string Uso = "Uso: /acoes | /acoes aprovar <id> <hash> | /acoes recusar <id> [motivo]";
    internal const int TamanhoDoIdCurto = 8;

    private static ProprietarioDaExecucao Dono(long idDoUsuario) => new(LedgerChannel.Telegram, idDoUsuario);

    // /acoes: cada pedido em aberto numa mensagem própria, com os botões quando pendente.
    // /acoes aprovar|recusar <id> …: o equivalente textual dos botões.
    public async Task<string?> AtenderAsync(long chatId, long idDoUsuario, string argumentos,
        CancellationToken cancellationToken)
    {
        var partes = argumentos.Trim().Split([' ', '\t', '\n'], 3, StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length == 0) return await ListarAsync(chatId, idDoUsuario, cancellationToken);
        var acao = partes[0].ToLowerInvariant();
        if (acao is not ("aprovar" or "recusar") || partes.Length < 2 || (acao == "aprovar" && partes.Length < 3))
            return Uso;
        var resto = partes.Length > 2 ? partes[2].Trim() : null;
        return await ExecutarAsync(async servicos =>
        {
            var aprovacoes = servicos.GetRequiredService<IAprovacoesDeAcaoAppService>();
            if (await ResolverIdAsync(aprovacoes, idDoUsuario, partes[1], cancellationToken) is not { } id)
                return $"Pedido {partes[1]} não encontrado entre os seus em aberto. Veja /acoes.";
            var decisao = acao == "aprovar"
                ? await aprovacoes.AprovarAsync(Dono(idDoUsuario), id, resto!.Split([' ', '\t', '\n'])[0], cancellationToken)
                : await aprovacoes.RecusarAsync(Dono(idDoUsuario), id, string.IsNullOrWhiteSpace(resto) ? null : resto,
                    cancellationToken);
            return Decisao(decisao);
        });
    }

    // Botão: o callback leva o pedido e o hash que esta mensagem mostrou; a Application confere o resto.
    internal Task<string> DecidirAsync(long idDoUsuario, TelegramAcaoCallback botao, CancellationToken cancellationToken) =>
        ExecutarAsync(async servicos =>
        {
            var aprovacoes = servicos.GetRequiredService<IAprovacoesDeAcaoAppService>();
            var decisao = botao.Aprovar
                ? await aprovacoes.AprovarAsync(Dono(idDoUsuario), botao.Id, botao.Hash, cancellationToken)
                : await aprovacoes.RecusarAsync(Dono(idDoUsuario), botao.Id, null, cancellationToken);
            return Decisao(decisao);
        });

    // Aviso ao dono de um pedido encerrado pela manutenção (expirado ou incerto depois de reinício).
    public async Task AvisarAsync(AprovacaoDeAcaoDto aprovacao, CancellationToken cancellationToken)
    {
        if (aprovacao.CanalDoDono != LedgerChannel.Telegram) return;
        var texto = aprovacao.Estado switch
        {
            EstadoDaAprovacaoDeAcao.Expirada =>
                $"⌛ O pedido de ação {IdCurto(aprovacao.Id)} expirou sem ser executado: {aprovacao.Descricao}. " +
                "Nada foi feito; peça de novo se ainda quiser.",
            EstadoDaAprovacaoDeAcao.Incerto =>
                $"⚠️ A ação {IdCurto(aprovacao.Id)} ({aprovacao.Descricao}) foi interrompida por um reinício: o efeito não " +
                "é conhecido e ela não será repetida. Confira o estado real antes de pedir de novo.",
            _ => null
        };
        if (texto is null) return;
        try
        {
            await telegram.SendMessageAsync(aprovacao.IdDoDono, texto, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Falha ao avisar sobre a ação {Aprovacao} ({ErrorType}).", IdCurto(aprovacao.Id),
                exception.GetType().Name);
        }
    }

    private async Task<string?> ListarAsync(long chatId, long idDoUsuario, CancellationToken cancellationToken)
    {
        IReadOnlyList<AprovacaoDeAcaoDto> lista;
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            lista = await escopo.ServiceProvider.GetRequiredService<IAprovacoesDeAcaoAppService>()
                .ListarEmAbertoAsync(Dono(idDoUsuario), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Consulta de aprovações de ação falhou ({ErrorType}).", exception.GetType().Name);
            return Indisponivel;
        }
        if (lista.Count == 0) return "Nenhum pedido de ação aguardando você.";
        foreach (var aprovacao in lista)
        {
            var pendente = aprovacao.Estado == EstadoDaAprovacaoDeAcao.Pendente;
            if (pendente && telegram.SupportsInlineKeyboards)
                await telegram.SendApprovalAsync(chatId, Texto(aprovacao), Teclado(aprovacao), cancellationToken);
            else
                await telegram.SendMessageAsync(chatId, Texto(aprovacao) + (pendente ? "\n" + Comandos(aprovacao) : ""),
                    cancellationToken);
        }
        return null;
    }

    // Resumo redigido: ação, alvo, execução, solicitante, parâmetros (já redigidos e cortados pela Application), hash
    // curto e prazo. O payload bruto nunca chega aqui.
    internal static string Texto(AprovacaoDeAcaoDto a)
    {
        var linhas = new List<string>
        {
            $"🔐 Ação {IdCurto(a.Id)}: {a.Descricao}",
            $"Ação: {a.Acao} ({Risco(a.Risco)}){(a.Alvo is null ? "" : $" em {a.Alvo}")}" +
            (a.CodigoDaExecucao is null ? "" : $", execução {a.CodigoDaExecucao}"),
            $"Pedida por: {a.Solicitante}"
        };
        linhas.AddRange(a.Parametros.Select(p => $"• {p.Nome}: {p.Valor}"));
        linhas.Add($"Hash: {a.HashCurto}");
        linhas.Add(a.Estado == EstadoDaAprovacaoDeAcao.Pendente
            ? $"Aguardando sua decisão até {Momento(a.ExpiraEm)}. Aprovar autoriza uma execução deste conteúdo exato."
            : $"Aprovada; aguardando execução até {Momento(a.ExpiraEm)}.");
        return string.Join('\n', linhas);
    }

    internal static TelegramInlineKeyboard Teclado(AprovacaoDeAcaoDto a) => new(
    [
        [new TelegramInlineButton($"Aprovar {a.HashCurto}", TelegramAcaoCallback.Aprovacao(a.Id, a.HashCurto))],
        [new TelegramInlineButton("Recusar", TelegramAcaoCallback.Recusa(a.Id))]
    ]);

    private static string Comandos(AprovacaoDeAcaoDto a) =>
        $"/acoes aprovar {IdCurto(a.Id)} {a.HashCurto}\n/acoes recusar {IdCurto(a.Id)} [motivo]";

    internal static string Decisao(DecisaoDeAcaoDto decisao)
    {
        var id = decisao.Aprovacao is { } a ? IdCurto(a.Id) : "";
        return decisao.Desfecho switch
        {
            DesfechoDaDecisaoDeAcao.Aprovada => $"✅ Ação {id} aprovada (hash {decisao.Aprovacao!.HashCurto}): uma " +
                "execução deste conteúdo exato.",
            DesfechoDaDecisaoDeAcao.Recusada => $"❌ Ação {id} recusada; nada será executado.",
            DesfechoDaDecisaoDeAcao.Expirada => $"⌛ O pedido {id} expirou antes da decisão; nada foi autorizado.",
            DesfechoDaDecisaoDeAcao.Cancelada => $"🚫 O pedido {id} foi cancelado: {decisao.Aprovacao!.Motivo}",
            DesfechoDaDecisaoDeAcao.JaDecidida => "Este pedido já foi decidido ou encerrado; a decisão não muda.",
            _ => "Pedido não encontrado."
        };
    }

    internal static string IdCurto(Guid id) => id.ToString("N", CultureInfo.InvariantCulture)[..TamanhoDoIdCurto];

    // O id curto só localiza o pedido entre os em aberto do próprio usuário; quem confere o dono é a Application.
    private static async Task<Guid?> ResolverIdAsync(IAprovacoesDeAcaoAppService aprovacoes, long idDoUsuario, string texto,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(texto, out var completo)) return completo;
        if (texto.Length < TamanhoDoIdCurto) return null;
        var candidatos = (await aprovacoes.ListarEmAbertoAsync(Dono(idDoUsuario), cancellationToken))
            .Where(a => a.Id.ToString("N", CultureInfo.InvariantCulture)
                .StartsWith(texto.ToLowerInvariant(), StringComparison.Ordinal)).ToArray();
        return candidatos.Length == 1 ? candidatos[0].Id : null;
    }

    private async Task<string> ExecutarAsync(Func<IServiceProvider, Task<string>> acao)
    {
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            return await acao(escopo.ServiceProvider);
        }
        catch (ConflitoDeConcorrenciaException)
        {
            return "Este pedido já foi decidido ou encerrado; a decisão não muda.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Recusas do domínio (hash diferente do exibido, por exemplo) já vêm em texto para a pessoa.
            return exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Decisão de ação protegida falhou ({ErrorType}).", exception.GetType().Name);
            return Indisponivel;
        }
    }

    private static string Risco(ClasseDeRisco risco) => risco switch
    {
        ClasseDeRisco.Leitura => "leitura",
        ClasseDeRisco.EscritaInterna => "escrita local",
        _ => "efeito externo"
    };

    private static string Momento(DateTimeOffset quando) =>
        quando.ToUniversalTime().ToString("dd/MM HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
