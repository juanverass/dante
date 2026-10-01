namespace Dante.Worker.Telegram;

public interface ITelegramBotApi
{
    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken);

    Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken);

    // The default keeps lightweight transports compatible with the textual approval fallback.
    async Task<long?> SendApprovalAsync(long chatId, string text, TelegramInlineKeyboard? keyboard,
        CancellationToken cancellationToken)
    {
        await SendMessageAsync(chatId, text, cancellationToken);
        return null;
    }

    Task EditApprovalAsync(long chatId, long messageId, string text, CancellationToken cancellationToken)
        => Task.CompletedTask;

    Task AnswerCallbackAsync(string callbackId, string text, CancellationToken cancellationToken)
        => Task.CompletedTask;

    // Best-effort presence signal ("typing"); implementations without it simply show nothing.
    Task SendChatActionAsync(long chatId, string action, CancellationToken cancellationToken) => Task.CompletedTask;
}
