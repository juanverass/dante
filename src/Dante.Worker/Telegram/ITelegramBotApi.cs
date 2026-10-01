namespace Dante.Worker.Telegram;

public interface ITelegramBotApi
{
    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken);

    Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken);

    // Best-effort presence signal ("typing"); implementations without it simply show nothing.
    Task SendChatActionAsync(long chatId, string action, CancellationToken cancellationToken) => Task.CompletedTask;
}
