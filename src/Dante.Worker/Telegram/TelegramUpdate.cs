using System.Text.Json.Serialization;

namespace Dante.Worker.Telegram;

public sealed record TelegramUpdate(
    [property: JsonPropertyName("update_id")] long UpdateId,
    [property: JsonPropertyName("message")] TelegramMessage? Message,
    [property: JsonPropertyName("callback_query")] TelegramCallbackQuery? CallbackQuery = null);

public sealed record TelegramMessage(
    [property: JsonPropertyName("chat")] TelegramChat Chat,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("from")] TelegramUser? From,
    [property: JsonPropertyName("message_id")] long MessageId = 0,
    [property: JsonPropertyName("caption")] string? Caption = null,
    [property: JsonPropertyName("media_group_id")] string? MediaGroupId = null,
    [property: JsonPropertyName("photo")] IReadOnlyList<TelegramPhotoSize>? Photo = null,
    [property: JsonPropertyName("document")] TelegramFileInfo? Document = null,
    [property: JsonPropertyName("voice")] TelegramFileInfo? Voice = null,
    [property: JsonPropertyName("audio")] TelegramFileInfo? Audio = null,
    [property: JsonPropertyName("video")] TelegramFileInfo? Video = null,
    [property: JsonPropertyName("video_note")] TelegramFileInfo? VideoNote = null,
    [property: JsonPropertyName("animation")] TelegramFileInfo? Animation = null,
    [property: JsonPropertyName("reply_to_message")] TelegramMessage? ReplyToMessage = null,
    [property: JsonPropertyName("message_thread_id")] long? MessageThreadId = null)
{
    public bool HasMedia => Photo is { Count: > 0 } || Document is not null || Voice is not null || Audio is not null ||
        Video is not null || VideoNote is not null || Animation is not null;
}

// One resolution of a photo; Telegram lists several, from the thumbnail to the largest.
public sealed record TelegramPhotoSize(
    [property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("file_size")] long? FileSize = null);

// The fields shared by document, voice, audio, video, video_note and animation. Name and MIME type are declared by
// the sender and are never trusted for storage: the D.A.N.T.E. names files itself and checks the real content.
public sealed record TelegramFileInfo(
    [property: JsonPropertyName("file_id")] string FileId,
    [property: JsonPropertyName("file_size")] long? FileSize = null,
    [property: JsonPropertyName("mime_type")] string? MimeType = null,
    [property: JsonPropertyName("file_name")] string? FileName = null);

public sealed record TelegramChat(
    [property: JsonPropertyName("id")] long Id);

public sealed record TelegramUser(
    [property: JsonPropertyName("id")] long Id);

public sealed record TelegramCallbackQuery(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("from")] TelegramUser From,
    [property: JsonPropertyName("message")] TelegramMessage? Message,
    [property: JsonPropertyName("data")] string? Data);

public sealed record TelegramInlineButton(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("callback_data")] string CallbackData);

public sealed record TelegramInlineKeyboard(
    [property: JsonPropertyName("inline_keyboard")] IReadOnlyList<IReadOnlyList<TelegramInlineButton>> Rows);
