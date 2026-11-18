using System.Globalization;
using Dante.Application.Comum;
using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Worker.Execucoes;

namespace Dante.Worker.Telegram;

// Adapter de entrada do acompanhamento e do controle de execuções (#305): interpreta /execucoes, /execucao, /aguardando
// e o /cancel de execuções, chama os casos de uso da Application num escopo próprio e formata a resposta. Nenhuma regra
// vive aqui: proprietário, autorização, redaction, classificação da espera e cancelamento são da Application. Só existe
// com banco e Orquestracao:Habilitada; sem ele, os comandos respondem que o acompanhamento não está habilitado.
public sealed class TelegramExecucoes(IServiceScopeFactory escopos, ILogger<TelegramExecucoes> logger,
    TimeProvider? relogio = null)
{
    public const string Desabilitado =
        "Acompanhamento de execuções não está habilitado neste Worker (depende do ledger e da fila durável).";
    public const string Indisponivel = "Acompanhamento de execuções indisponível (banco de dados). Tente novamente.";
    internal const string UsoDaListagem = "Uso: /execucoes [todas]";
    internal const string UsoDoDetalhe = "Uso: /execucao <J…|E…> | /execucao aprovar|corrigir <E…> <hash> [instrução] | " +
        "/execucao recusar <E…> [motivo] | /execucao revisar <E…> | /execucao objetivo <E…> <tarefa>";

    private readonly TimeProvider relogio = relogio ?? TimeProvider.System;

    private static ProprietarioDaExecucao Dono(long idDoUsuario) => new(LedgerChannel.Telegram, idDoUsuario);

    public Task<string> ListarAsync(long idDoUsuario, string argumentos, CancellationToken cancellationToken)
    {
        var todas = argumentos.Trim().Equals("todas", StringComparison.OrdinalIgnoreCase);
        if (!todas && argumentos.Trim().Length > 0) return Task.FromResult(UsoDaListagem);
        return ExecutarAsync(async servicos =>
        {
            var lista = await servicos.GetRequiredService<IAcompanhamentoDeExecucoesAppService>()
                .ListarAsync(Dono(idDoUsuario), todas, cancellationToken);
            return lista.Count == 0 ? "Nenhuma execução registrada."
                : "Execuções:\n" + string.Join('\n', lista.Select(Linha)) + "\nDetalhes: /execucao <código>";
        });
    }

    public Task<string> DetalharAsync(long idDoUsuario, string argumentos, CancellationToken cancellationToken)
    {
        // Decisões do gate de um roteiro (#308): /execucao aprovar|corrigir|recusar|objetivo E… …
        var partes = argumentos.Trim().Split([' ', '\t', '\n'], 3, StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length > 0 && partes[0].ToLowerInvariant() is "aprovar" or "corrigir" or "recusar" or "revisar" or "objetivo")
            return DecidirAsync(idDoUsuario, partes, cancellationToken);
        var codigo = argumentos.Trim();
        if (codigo.Length == 0 || codigo.IndexOfAny([' ', '\t', '\r', '\n']) >= 0) return Task.FromResult(UsoDoDetalhe);
        return ExecutarAsync(async servicos =>
        {
            if (await servicos.GetRequiredService<IAcompanhamentoDeExecucoesAppService>()
                    .ObterAsync(Dono(idDoUsuario), codigo, cancellationToken) is not { } detalhe)
                return NaoEncontrada(codigo);
            var texto = Detalhe(detalhe);
            if (detalhe.Resumo.Tipo == TipoDeExecucao.Roteiro &&
                await servicos.GetRequiredService<IRoteirosAppService>().ObterDoProprietarioAsync(Dono(idDoUsuario), codigo,
                    cancellationToken)
                    is { } roteiro)
                texto += "\n" + Roteiro(roteiro);
            return texto;
        });
    }

    // Gate do roteiro (#308): só interpreta e chama o caso de uso; dono, integridade, hash e rodadas são da Application.
    private Task<string> DecidirAsync(long idDoUsuario, string[] partes, CancellationToken cancellationToken)
    {
        var acao = partes[0].ToLowerInvariant();
        if (partes.Length < 2) return Task.FromResult(UsoDoDetalhe);
        var codigo = partes[1];
        var resto = partes.Length > 2 ? partes[2].Trim() : "";
        return ExecutarAsync(servicos => RecusaComoTextoAsync(async () =>
        {
            var roteiros = servicos.GetRequiredService<IRoteirosAppService>();
            if (acao == "objetivo")
                return await roteiros.SubstituirObjetivoAsync(Dono(idDoUsuario), codigo, resto, cancellationToken)
                    ? $"{codigo.ToUpperInvariant()}: tarefa recebida; o roteiro volta à fila."
                    : $"{codigo.ToUpperInvariant()} não está aguardando uma tarefa nova. Veja /execucao {codigo}.";
            if (acao == "revisar")
                return Decisao(await roteiros.RevisarNovamenteAsync(Dono(idDoUsuario), codigo, cancellationToken), codigo);
            DecisaoHumanaDto decisao;
            if (acao == "recusar") decisao = new(TipoDeDecisaoHumana.Recusar, Texto: resto.Length == 0 ? null : resto);
            else
            {
                var hashETexto = resto.Split([' ', '\t', '\n'], 2, StringSplitOptions.RemoveEmptyEntries);
                if (hashETexto.Length == 0) return UsoDoDetalhe;
                decisao = new(acao == "aprovar" ? TipoDeDecisaoHumana.Aprovar : TipoDeDecisaoHumana.Corrigir, hashETexto[0],
                    hashETexto.Length > 1 ? hashETexto[1] : null);
            }
            return Decisao(await roteiros.DecidirAsync(Dono(idDoUsuario), codigo, decisao, cancellationToken), codigo);
        }), conflito: "A execução mudou durante a decisão. Consulte /execucao e tente novamente.");
    }

    private static string Decisao(ResultadoDaDecisaoDto resultado, string codigo) =>
        resultado.Resultado switch
        {
            ResultadoDaDecisao.ArtefatoDivergente => $"{resultado.Codigo}: o conteúdo da worktree mudou depois da " +
                $"revisão (agora {resultado.HashAtual![..12]}); nada foi decidido e o gate continua pendente. Peça uma " +
                $"nova revisão do conteúdo atual: /execucao revisar {resultado.Codigo}",
            ResultadoDaDecisao.RevisaoAgendada => $"{resultado.Codigo}: nova revisão da rodada {resultado.Rodada} " +
                $"agendada para o conteúdo atual ({resultado.HashAtual![..12]}); a decisão volta a você depois dela.",
            ResultadoDaDecisao.Aprovada => $"{resultado.Codigo} aprovada (rodada {resultado.Rodada}) e encerrada. " +
                "Nada foi commitado nem enviado: as alterações ficam na worktree, preservada para a entrega.",
            ResultadoDaDecisao.CorrecaoAgendada => $"{resultado.Codigo}: correção agendada (rodada {resultado.Rodada}); " +
                "o roteiro volta à fila e continua quando houver vaga de escrita.",
            ResultadoDaDecisao.Recusada => $"{resultado.Codigo} recusada e encerrada; a worktree alterada é preservada.",
            ResultadoDaDecisao.SemPedidoPendente => $"{resultado.Codigo} não está aguardando aprovação (ou já foi decidida).",
            _ => NaoEncontrada(codigo)
        };

    // Recusas do caso de uso (hash errado, instrução com credencial, sem rodada) já vêm em texto para o usuário; falha de
    // infraestrutura continua indo ao tratamento comum (indisponível).
    private static async Task<string> RecusaComoTextoAsync(Func<Task<string>> acao)
    {
        try
        {
            return await acao();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return exception.Message;
        }
    }

    private static string Roteiro(RoteiroDaExecucaoDto r)
    {
        var linhas = new List<string>
        {
            $"Grupo: {r.NomeDoGrupo} v{r.NumeroDaVersaoDoGrupo} (snapshot {r.HashDoSnapshot[..12]}); " +
            $"rodadas de correção: até {r.RodadasDeRevisaoMaximas}",
            "Participantes: " + string.Join(", ", r.Participantes.Select(p =>
                $"{p.Papel} = {p.Handle} v{p.NumeroDaVersao}{(p.PodeEscrever ? " (escrita)" : "")}"))
        };
        if (r.ObjetivoAlteradoPelaRedacao) linhas.Add("A tarefa foi protegida pela redação; o original não foi guardado.");
        linhas.Add("Passos: " + string.Join(" → ", r.Etapas.Select(e => $"{Passo(e.Passo)} r{e.Rodada} ({Estado(e.Estado)})")));
        if (r.PedidoPendente is { } pedido)
            linhas.Add($"Aguardando sua decisão sobre o resultado da rodada {pedido.Rodada} (hash {pedido.HashDoArtefato[..12]}):\n" +
                $"/execucao aprovar {r.Codigo} {pedido.HashDoArtefato[..12]}\n" +
                $"/execucao corrigir {r.Codigo} {pedido.HashDoArtefato[..12]} <o que corrigir>\n" +
                $"/execucao recusar {r.Codigo} [motivo]\n" +
                "Aprovar só aceita o resultado: não faz commit, push, PR nem merge.");
        if (r.Estado == EstadoDaExecucao.AguardandoEntrada)
            linhas.Add($"Envie a tarefa de novo, sem credenciais: /execucao objetivo {r.Codigo} <tarefa>");
        return string.Join('\n', linhas);
    }

    private static string Passo(Dante.Domain.Grupos.TipoDePassoDoRoteiro passo) => passo switch
    {
        Dante.Domain.Grupos.TipoDePassoDoRoteiro.Implementar => "implementar",
        Dante.Domain.Grupos.TipoDePassoDoRoteiro.Revisar => "revisar",
        Dante.Domain.Grupos.TipoDePassoDoRoteiro.Corrigir => "corrigir",
        _ => "aprovação"
    };

    public Task<string> AguardandoAsync(long idDoUsuario, string argumentos, CancellationToken cancellationToken)
    {
        if (argumentos.Trim().Length > 0) return Task.FromResult("Uso: /aguardando");
        return ExecutarAsync(async servicos =>
        {
            var lista = await servicos.GetRequiredService<IAcompanhamentoDeExecucoesAppService>()
                .ListarAguardandoAsync(Dono(idDoUsuario), cancellationToken);
            return lista.Count == 0 ? "Nenhuma execução aguardando."
                : "Aguardando:\n" + string.Join('\n', lista.Select(r => $"{Linha(r)}\n  ↳ {Espera(r)}"));
        });
    }

    // Linha do /status; nula sem execuções ativas ou com o banco indisponível (o /status nunca falha por ela).
    public async Task<string?> ResumoParaStatusAsync(long idDoUsuario, CancellationToken cancellationToken)
    {
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            var painel = await escopo.ServiceProvider.GetRequiredService<IAcompanhamentoDeExecucoesAppService>()
                .ResumirAsync(Dono(idDoUsuario), cancellationToken);
            return painel.EmAndamento + painel.Aguardando == 0 ? null
                : $"Execuções: {painel.EmAndamento} em andamento, {painel.Aguardando} aguardando (/execucoes, /aguardando).";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Resumo de execuções indisponível no /status ({ErrorType}).", exception.GetType().Name);
            return "Execuções: indisponível (banco de dados).";
        }
    }

    // /cancel de uma execução no ledger: E…, ou J… que o registro do job em memória não tem (boot anterior).
    // naoEncontrada: a resposta do legado para "inexistente", reutilizada para não distinguir execução de outro dono.
    public Task<string> CancelarAsync(long idDoUsuario, string codigo, string? motivo, string naoEncontrada,
        CancellationToken cancellationToken) =>
        ExecutarAsync(async servicos =>
        {
            var resultado = await servicos.GetRequiredService<IControleDeExecucoesAppService>()
                .SolicitarCancelamentoAsync(Dono(idDoUsuario), codigo, motivo, cancellationToken);
            return resultado.Resultado switch
            {
                ResultadoDoCancelamento.Cancelada => $"{resultado.Codigo} cancelada. Nenhum processo foi iniciado.",
                ResultadoDoCancelamento.Solicitada =>
                    $"Cancelamento de {resultado.Codigo} solicitado. A execução em andamento será encerrada e você receberá um aviso.",
                ResultadoDoCancelamento.JaSolicitada => $"Cancelamento de {resultado.Codigo} já havia sido solicitado.",
                ResultadoDoCancelamento.JaEncerrada when resultado.Codigo!.StartsWith('E') =>
                    $"{resultado.Codigo} já está encerrada ({Estado(resultado.Estado!.Value)}).",
                _ => naoEncontrada
            };
        }, conflito: "A execução mudou durante o cancelamento. Consulte /execucao e tente novamente.");

    internal static string NaoEncontrada(string codigo) => $"Execução {codigo} não encontrada. Veja /execucoes.";

    private async Task<string> ExecutarAsync(Func<IServiceProvider, Task<string>> acao, string? conflito = null)
    {
        try
        {
            await using var escopo = escopos.CreateAsyncScope();
            return await acao(escopo.ServiceProvider);
        }
        catch (ConflitoDeConcorrenciaException) when (conflito is not null)
        {
            return conflito;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Consulta ou controle de execuções falhou ({ErrorType}).", exception.GetType().Name);
            return Indisponivel;
        }
    }

    private string Linha(ResumoDaExecucaoDto r)
    {
        var partes = new List<string> { r.Codigo, Tipo(r.Tipo) };
        if (r.Agente is not null) partes.Add(r.Agente);
        partes.Add(Estado(r.Estado));
        if (r.EtapaAtual is { } etapa && !r.Terminal)
            partes.Add($"etapa {etapa}/{r.TotalDeEtapas}" + (r.Tentativa > 1 ? $" (tentativa {r.Tentativa})" : ""));
        partes.Add(Idade(r.EncerradaEm ?? r.CriadaEm, r.EncerradaEm is null ? "criada" : "encerrada"));
        if (r.CancelamentoSolicitado && !r.Terminal) partes.Add("cancelamento solicitado");
        return string.Join(" · ", partes);
    }

    private string Detalhe(DetalheDaExecucaoDto d)
    {
        var r = d.Resumo;
        var linhas = new List<string>
        {
            $"{r.Codigo} ({Tipo(r.Tipo)}{(r.Agente is null ? "" : $", {r.Agente}")})",
            $"Estado: {Estado(r.Estado)}" + (r.Espera is null ? "" : $" — {Espera(r)}"),
            $"Contexto: {Contexto(d)}",
            $"Criada: {Instante(r.CriadaEm)}" + (d.IniciadaEm is { } i ? $" · iniciada: {Instante(i)}" : "") +
                (r.EncerradaEm is { } e ? $" · encerrada: {Instante(e)}" : "")
        };
        if (d.Objetivo is not null) linhas.Add($"Pedido: {d.Objetivo}");
        if (d.Motivo is not null) linhas.Add($"Motivo: {d.Motivo}");
        if (d.Erro is not null) linhas.Add($"Erro: {d.Erro}");
        if (d.CancelamentoSolicitadoEm is { } pedido)
            linhas.Add($"Cancelamento solicitado: {Instante(pedido)}" +
                (d.MotivoDoCancelamento is null ? "" : $" ({d.MotivoDoCancelamento})"));
        linhas.Add("Etapas:");
        foreach (var etapa in d.Etapas)
        {
            var detalhes = new List<string> { Estado(etapa.Estado), $"tentativa {etapa.Tentativa}" };
            if (etapa.Agente is { } agente)
                detalhes.Add(agente.ToString().ToLowerInvariant() +
                    (etapa.Modelo is null ? "" : $" {etapa.Modelo}") + (etapa.Esforco is null ? "" : $"/{etapa.Esforco}"));
            detalhes.Add(etapa.Escrita ? "escrita" : "leitura");
            if (etapa.Sessao is not null) detalhes.Add($"sessão {etapa.Sessao}");
            if (etapa.CodigoDeSaida is { } saida) detalhes.Add($"saída {saida}");
            linhas.Add($"{etapa.Numero}. " + string.Join(" · ", detalhes));
            if (etapa.Motivo is not null && etapa.Motivo != d.Motivo) linhas.Add($"   motivo: {etapa.Motivo}");
            if (etapa.Erro is not null && etapa.Erro != d.Erro) linhas.Add($"   erro: {etapa.Erro}");
        }
        if (!r.Terminal && r.Tipo != TipoDeExecucao.Avulsa) linhas.Add($"Cancelar: /cancel {r.Codigo} [motivo]");
        return string.Join('\n', linhas);
    }

    private static string Tipo(TipoDeExecucao tipo) => tipo switch
    {
        TipoDeExecucao.Avulsa => "job",
        TipoDeExecucao.Roteiro => "roteiro de grupo",
        _ => "tarefa"
    };

    private static string Contexto(DetalheDaExecucaoDto d)
    {
        var principal = d.ModoDoContexto == "Repository" && d.AliasDoContexto is not null ? d.AliasDoContexto : "General";
        return d.AliasesVinculados.Count == 0 ? principal : string.Join(" + ", d.AliasesVinculados.Prepend(principal));
    }

    private static string Espera(ResumoDaExecucaoDto r) => r.Espera switch
    {
        MotivoDaEspera.NaFila => "na fila, aguardando um slot de execução",
        MotivoDaEspera.AguardandoNovaTentativa => "aguardando nova tentativa depois de uma falha transitória",
        MotivoDaEspera.Interrompida => $"interrompida; não é retomada sozinha (cancele com /cancel {r.Codigo})",
        MotivoDaEspera.Bloqueada =>
            $"bloqueada: resultado incerto, confira o efeito antes de continuar (cancele com /cancel {r.Codigo})",
        MotivoDaEspera.AguardandoAprovacao => "aguardando aprovação",
        MotivoDaEspera.AguardandoEntrada => "aguardando entrada",
        MotivoDaEspera.AguardandoDelegacoes => "aguardando delegações",
        _ => "em andamento"
    };

    internal static string Estado(EstadoDaExecucao estado) => estado switch
    {
        EstadoDaExecucao.Pendente => "pendente",
        EstadoDaExecucao.EmExecucao => "em execução",
        EstadoDaExecucao.AguardandoAprovacao => "aguardando aprovação",
        EstadoDaExecucao.AguardandoEntrada => "aguardando entrada",
        EstadoDaExecucao.AguardandoDelegacoes => "aguardando delegações",
        EstadoDaExecucao.Bloqueada => "bloqueada",
        EstadoDaExecucao.Interrompida => "interrompida",
        EstadoDaExecucao.Concluida => "concluída",
        EstadoDaExecucao.Falhou => "falhou",
        _ => "cancelada"
    };

    private static string Estado(EstadoDaEtapa estado) => estado switch
    {
        EstadoDaEtapa.Pendente => "pendente",
        EstadoDaEtapa.Pronta => "pronta",
        EstadoDaEtapa.EmExecucao => "em execução",
        EstadoDaEtapa.AguardandoAprovacao => "aguardando aprovação",
        EstadoDaEtapa.AguardandoEntrada => "aguardando entrada",
        EstadoDaEtapa.Interrompida => "interrompida",
        EstadoDaEtapa.Concluida => "concluída",
        EstadoDaEtapa.Falhou => "falhou",
        EstadoDaEtapa.Ignorada => "ignorada",
        _ => "cancelada"
    };

    private string Idade(DateTimeOffset instante, string rotulo)
    {
        var decorrido = relogio.GetUtcNow() - instante;
        if (decorrido < TimeSpan.Zero) decorrido = TimeSpan.Zero;
        var texto = decorrido.TotalMinutes < 1 ? "agora"
            : decorrido.TotalHours < 1 ? $"há {(int)decorrido.TotalMinutes} min"
            : decorrido.TotalDays < 1 ? $"há {(int)decorrido.TotalHours} h"
            : $"há {(int)decorrido.TotalDays} d";
        return $"{rotulo} {texto}";
    }

    private static string Instante(DateTimeOffset instante) =>
        instante.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
}
