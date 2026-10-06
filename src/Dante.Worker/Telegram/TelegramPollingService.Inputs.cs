using Dante.Worker.Sessions;

namespace Dante.Worker.Telegram;

public sealed partial class TelegramPollingService
{
    private const string InputUnavailable = "Essa solicitação não está disponível, já foi respondida ou expirou. Responda à mensagem de uma pergunta pendente.";

    private async Task HandleInputReplyAsync(TelegramMessage message, CancellationToken cancellationToken)
    {
        var reply = message.ReplyToMessage!;
        var correlation = reply.Chat.Id == message.Chat.Id
            ? delivery.FindInputMessage(message.From!.Id, message.Chat.Id, reply.MessageId) : null;
        // A reply to any other message may select it for the Brain (#145, e.g. "documente isso" on an agent's answer).
        if (correlation is null && brain is not null && message.Text is { } text &&
            await brain.AtenderAsync(message, text.Trim(), sessions?.GetActive(message.From!.Id)?.Id, cancellationToken)
                is { } respostaBrain)
        {
            await SendLongMessageAsync(message.Chat.Id, respostaBrain, cancellationToken);
            return;
        }
        var request = correlation is null ? null : sessions?.GetPendingRequest(message.From!.Id, correlation.RequestId);
        if (request is null || request.IsApproval || request.SessionId != correlation!.SessionId ||
            request.TurnId != correlation.TurnId)
        {
            await SendReplyAsync(message.Chat.Id, InputUnavailable, cancellationToken);
            return;
        }
        if (!TryInputResponse(request, message.Text?.Trim() ?? "", out var response, out var error))
        {
            await SendReplyAsync(message.Chat.Id, error, cancellationToken);
            return;
        }
        var result = await sessions!.RespondAsync(message.From!.Id, request.SessionId, request.TurnId,
            request.RequestId, response, cancellationToken);
        await SendReplyAsync(message.Chat.Id, result.Accepted ? "Resposta enviada." : InputUnavailable, cancellationToken);
    }

    private async Task<string> HandleInputCallbackAsync(TelegramCallbackQuery callback, TelegramInputCallback action,
        CancellationToken cancellationToken)
    {
        var message = callback.Message!;
        var request = sessions!.GetPendingRequest(callback.From.Id, action.RequestId);
        if (request is null || request.IsApproval || request.Questions.Count != 1 ||
            action.Option >= request.Questions[0].Options.Count ||
            !delivery.OwnsInputButton(action.RequestId, callback.From.Id, message.Chat.Id, message.MessageId))
            return InputUnavailable;
        var question = request.Questions[0];
        var response = new AgentInputResponse(new Dictionary<string, string> { [question.Id] = question.Options[action.Option] });
        var result = await sessions.RespondAsync(callback.From.Id, action.SessionId, action.TurnId,
            action.RequestId, response, cancellationToken);
        return result.Accepted ? "Resposta enviada." : InputUnavailable;
    }

    private static bool TryInputResponse(AgentPendingRequest request, string text,
        out AgentInputResponse response, out string error)
    {
        response = null!;
        var answers = request.Questions.Count == 1 ? [text] : text.Split('|', StringSplitOptions.TrimEntries);
        error = request.Questions.Count == 1 ? "Envie uma resposta em texto usando Reply a esta mensagem." :
            $"Informe {request.Questions.Count} resposta(s) na ordem das perguntas, separadas por |. " +
            "Exemplo com duas perguntas: primeira resposta | segunda resposta.";
        if (answers.Length != request.Questions.Count || answers.Any(string.IsNullOrWhiteSpace)) return false;
        response = new AgentInputResponse(request.Questions.Select((question, index) =>
            (question.Id, Answer: answers[index])).ToDictionary(item => item.Id, item => item.Answer));
        return true;
    }
}
