using Dante.Application.Execucoes;
using Dante.Domain.Execucoes;
using Dante.Worker.Sessions;

namespace Dante.Worker.Execucoes;

// Política de eventos-chave da sessão (AD-66): turnos, ferramentas, solicitações e uso de tokens. Deltas, texto de
// mensagem, avisos, erros, descrições de comando e caminhos de arquivo não são persistidos.
internal static class ProjecaoDeEventosDaSessao
{
    internal static EventoDeSessaoDto? Projetar(long numeroDaSessao, AgentEvent evento)
    {
        (TipoDeEventoDeOrquestracao Tipo, Dictionary<string, string?>? Dados)? projetado = evento switch
        {
            TurnStartedEvent => (TipoDeEventoDeOrquestracao.TurnoIniciado, null),
            TurnCompletedEvent concluido => (TipoDeEventoDeOrquestracao.TurnoConcluido, new()
            {
                ["resultado"] = concluido.Outcome.ToString(),
                ["tokensDeEntrada"] = concluido.Usage?.InputTokens?.ToString(),
                ["tokensDeSaida"] = concluido.Usage?.OutputTokens?.ToString(),
                ["tokensEmCache"] = concluido.Usage?.CachedInputTokens?.ToString()
            }),
            ToolStartedEvent ferramenta => (TipoDeEventoDeOrquestracao.FerramentaIniciada, new()
            {
                ["item"] = ferramenta.ItemId,
                ["tipo"] = ferramenta.Kind.ToString(),
                ["servidor"] = ferramenta.Server,
                ["ferramenta"] = ferramenta.ToolName
            }),
            ToolCompletedEvent ferramenta => (TipoDeEventoDeOrquestracao.FerramentaConcluida, new()
            {
                ["item"] = ferramenta.ItemId,
                ["tipo"] = ferramenta.Kind.ToString(),
                ["sucesso"] = ferramenta.Succeeded ? "true" : "false"
            }),
            ApprovalRequestedEvent aprovacao => (TipoDeEventoDeOrquestracao.AprovacaoSolicitada, new()
            {
                ["solicitacao"] = aprovacao.RequestId,
                ["tipo"] = aprovacao.Kind.ToString()
            }),
            UserInputRequestedEvent entrada => (TipoDeEventoDeOrquestracao.EntradaSolicitada, new()
            {
                ["solicitacao"] = entrada.RequestId,
                ["perguntas"] = entrada.Questions.Count.ToString()
            }),
            RequestResolvedEvent resolvida => (TipoDeEventoDeOrquestracao.SolicitacaoResolvida, new()
            {
                ["solicitacao"] = resolvida.RequestId,
                ["decisao"] = resolvida.Decision.ToString()
            }),
            InputResolvedEvent resolvida => (TipoDeEventoDeOrquestracao.SolicitacaoResolvida, new()
            {
                ["solicitacao"] = resolvida.RequestId
            }),
            RequestClosedEvent encerrada => (TipoDeEventoDeOrquestracao.SolicitacaoEncerrada, new()
            {
                ["solicitacao"] = encerrada.RequestId,
                ["motivo"] = "encerrada"
            }),
            RequestExpiredEvent expirada => (TipoDeEventoDeOrquestracao.SolicitacaoEncerrada, new()
            {
                ["solicitacao"] = expirada.RequestId,
                ["motivo"] = "expirada"
            }),
            _ => null
        };
        return projetado is { } valor
            ? new EventoDeSessaoDto(numeroDaSessao, valor.Tipo, evento.TurnId, valor.Dados,
                evento.TimestampUtc == default ? DateTimeOffset.UtcNow : evento.TimestampUtc)
            : null;
    }
}
