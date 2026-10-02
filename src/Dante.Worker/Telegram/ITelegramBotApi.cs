namespace Dante.Worker.Telegram;

public interface ITelegramBotApi
{
    Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken);

    Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken);

    // Input delivery returns message ids, including messages without buttons, and supports inline options.
    // Lightweight transports retain the /input instructions unless they explicitly provide this contract.
    bool SupportsInputMessages => false;

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

    async Task<long?> SendFormattedMessageAsync(long chatId, TelegramFormattedMessage message,
        TelegramInlineKeyboard? keyboard, CancellationToken cancellationToken)
    {
        if (keyboard is not null) return await SendApprovalAsync(chatId, message.PlainText, keyboard, cancellationToken);
        await SendMessageAsync(chatId, message.PlainText, cancellationToken);
        return null;
    }

    // Best-effort presence signal ("typing"); implementations without it simply show nothing.
    Task SendChatActionAsync(long chatId, string action, CancellationToken cancellationToken) => Task.CompletedTask;

    // Copies a file sent to the bot into destination and returns the bytes written. More than maxBytes, declared or
    // real, throws TelegramFileTooLargeException before or while downloading.
    Task<long> DownloadFileAsync(string fileId, Stream destination, long maxBytes, CancellationToken cancellationToken)
        => throw new NotSupportedException("Download de arquivos indisponível.");

    // Uploads a local file by multipart: as a photo (Telegram shows a compressed preview) or as a document (the
    // original bytes, downloadable).
    Task SendFileAsync(long chatId, TelegramFileUpload upload, CancellationToken cancellationToken)
        => throw new NotSupportedException("Envio de arquivos indisponível.");
}

public sealed record TelegramFileUpload(string Path, string FileName, string MediaType, bool AsPhoto, string? Caption);

public sealed class TelegramFileTooLargeException() : IOException("Arquivo acima do limite de tamanho.");
