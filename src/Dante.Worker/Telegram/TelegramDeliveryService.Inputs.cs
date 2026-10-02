using Dante.Worker.Sessions;

namespace Dante.Worker.Telegram;

public sealed partial class TelegramDeliveryService
{
    private readonly Dictionary<string, List<RequestMessage>> inputs = new(StringComparer.OrdinalIgnoreCase);

    // This is delivery correlation, not request state. The SessionRegistry remains the authority for validity.
    public UserInputRequestedEvent? FindInputMessage(long userId, long chatId, long messageId)
    {
        lock (gate)
            return messageId > 0 ? inputs.Values.SelectMany(messages => messages).FirstOrDefault(message =>
                message.Record.UserId == userId && message.Record.ChatId == chatId && message.MessageId == messageId)?.Input : null;
    }

    public bool OwnsInputButton(string requestId, long userId, long chatId, long messageId)
    {
        lock (gate)
            return inputs.TryGetValue(requestId, out var messages) && messages.Any(message =>
                message.Record.UserId == userId && message.Record.ChatId == chatId &&
                message.MessageId == messageId && messageId > 0 && message.Status is null && message.Keyboard is not null);
    }

    private TelegramInlineKeyboard? InputKeyboard(UserInputRequestedEvent input, bool hideDetails, DeliveryRecord record)
    {
        if (!botApi.SupportsInputMessages || hideDetails) return null;
        var keyboard = TelegramInputCallback.Keyboard(input);
        // Unlike the message body, labels do not pass through the formatter/redactor.
        return keyboard is not null && keyboard.Rows.SelectMany(row => row)
            .Any(button => Secrets(record).Any(secret => button.Text.Contains(secret, StringComparison.Ordinal))) ? null : keyboard;
    }

    private string InputInstructions(UserInputRequestedEvent input, bool hideDetails, DeliveryRecord record)
    {
        var keyboard = InputKeyboard(input, hideDetails, record);
        var questions = hideDetails ? "Perguntas omitidas para proteger segredos do ambiente." :
            string.Join("\n\n", input.Questions.Select((question, index) =>
                (input.Questions.Count > 1 || !botApi.SupportsInputMessages ? $"{index + 1}. " : "") + question.Text +
                (keyboard is not null || question.Options.Count == 0 ? "" :
                    botApi.SupportsInputMessages ? "\nOpções: " + string.Join("; ", question.Options) :
                        " (opções: " + string.Join(", ", question.Options) + ")")));
        var instructions = input.Questions.Count > 1
            ? $"Responda às {input.Questions.Count} perguntas na ordem, separando as respostas por |.\n" +
              "Exemplo com duas perguntas: primeira resposta | segunda resposta.\n"
            : "";
        if (botApi.SupportsInputMessages)
            return "Preciso da sua resposta para continuar:\n\n" + questions + "\n\n" +
                (keyboard is null ? "Responda diretamente a esta mensagem usando Reply.\n" :
                    "Escolha um botão ou responda diretamente a esta mensagem com outra orientação.\n") +
                instructions + "Expira em 5 minutos.\n";

        var ids = $"{input.SessionId} {input.TurnId} {input.RequestId}";
        return $"Resposta pendente {ids}:\n{questions}\n{instructions}" +
            $"/input {ids} <resposta1>" + (input.Questions.Count > 1 ? " | <resposta2> ..." : "") +
            "\nExpira em 5 minutos.\n";
    }

    private void AddInputMessages(DeliveryRecord record, UserInputRequestedEvent input, string text, bool hideDetails,
        bool pending)
    {
        FlushBuffer(record);
        var parts = new TelegramMessageFormatter().Format(Redact(text, record), record.PrefixReserve + 80);
        var messages = new List<RequestMessage>();
        var keyboard = InputKeyboard(input, hideDetails, record);
        for (var index = 0; index < parts.Count; index++)
        {
            // Long requests retain Reply correlation on every part; only the last part offers the keyboard.
            var message = new RequestMessage(record, index == parts.Count - 1 ? keyboard : null, input);
            // Cancellation can beat the pump's publication of the request. The source snapshot already knows it ended.
            if (botApi.SupportsInputMessages && !pending) message.Status = "Solicitação encerrada.";
            record.RequestChunks[record.Chunks.Count] = message;
            record.Chunks.Add(parts[index]);
            messages.Add(message);
        }
        inputs[input.RequestId] = messages;
        record.HasVisibleText = true;
        record.ContentVersion++;
        if (record.State != TelegramDeliveryState.Failed)
        {
            record.State = TelegramDeliveryState.Pending;
            Schedule(record);
        }
    }

    private void UpdateInputMessages(string sessionId, AgentEvent agentEvent)
    {
        var requestId = agentEvent switch
        {
            InputResolvedEvent resolved => resolved.RequestId,
            RequestExpiredEvent expired => expired.RequestId,
            RequestClosedEvent closed => closed.RequestId,
            _ => null
        };
        var status = agentEvent switch
        {
            InputResolvedEvent => "✅ Resposta enviada",
            RequestExpiredEvent => "⌛ Solicitação expirada",
            _ => "Solicitação encerrada."
        };
        if (requestId is not null && inputs.TryGetValue(requestId, out var messages))
            foreach (var message in messages.Where(message => message.Status is null)) ResolveMessage(message, status);
        else if (agentEvent is TurnCompletedEvent or ErrorEvent)
            foreach (var message in inputs.Values.SelectMany(messages => messages)
                         .Where(message => message.Record.SessionId == sessionId && message.Status is null))
                ResolveMessage(message, status);
    }
}
