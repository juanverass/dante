using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Dante.Worker.Telegram;

public sealed class TelegramBotApi(HttpClient httpClient, IOptions<TelegramOptions> options) : ITelegramBotApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Telegram rejects optional fields sent as JSON null ("unsupported parse_mode", "object expected as reply
    // markup"); an unset option must be omitted from the request.
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(
        long offset,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MethodUrl("getUpdates"))
        {
            Content = JsonContent.Create(new
            {
                offset,
                timeout = 25,
                allowed_updates = new[] { "message", "callback_query" }
            })
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<TelegramEnvelope<TelegramUpdate[]>>(
            JsonOptions, cancellationToken);

        if (envelope is not { Ok: true, Result: not null })
        {
            throw new JsonException("Resposta inválida da API do Telegram.");
        }

        return envelope.Result;
    }

    public bool SupportsInlineKeyboards => true;

    public async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        await SendApprovalAsync(chatId, text, null, cancellationToken);
    }

    public async Task<long?> SendApprovalAsync(long chatId, string text, TelegramInlineKeyboard? keyboard,
        CancellationToken cancellationToken)
        => await SendMessageCoreAsync(chatId, text, keyboard, null, cancellationToken);

    public Task<long?> SendFormattedMessageAsync(long chatId, TelegramFormattedMessage message,
        TelegramInlineKeyboard? keyboard, CancellationToken cancellationToken)
        => SendMessageCoreAsync(chatId, message.Html, keyboard, "HTML", cancellationToken);

    private async Task<long?> SendMessageCoreAsync(long chatId, string text, TelegramInlineKeyboard? keyboard,
        string? parseMode, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MethodUrl("sendMessage"))
        {
            Content = JsonContent.Create(new { chat_id = chatId, text, reply_markup = keyboard, parse_mode = parseMode },
                options: RequestJsonOptions)
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ThrowIfRateLimitedAsync(response, cancellationToken);
        if (parseMode is not null && response.StatusCode == HttpStatusCode.BadRequest)
        {
            // Read Telegram's diagnostic only to classify the error; never log or propagate it (it may echo content).
            try
            {
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (error.RootElement.TryGetProperty("description", out var description) &&
                    description.GetString() is { } detail &&
                    (detail.Contains("parse entities", StringComparison.OrdinalIgnoreCase) ||
                     detail.Contains("unsupported start tag", StringComparison.OrdinalIgnoreCase) ||
                     detail.Contains("can't find end", StringComparison.OrdinalIgnoreCase)))
                    throw new TelegramMarkupException();
            }
            catch (JsonException) { }
        }
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<TelegramEnvelope<JsonElement>>(
            JsonOptions, cancellationToken);

        if (envelope is not { Ok: true })
        {
            throw new JsonException("Resposta inválida da API do Telegram.");
        }
        return envelope.Result.ValueKind == JsonValueKind.Object &&
            envelope.Result.TryGetProperty("message_id", out var messageId) ? messageId.GetInt64() : null;
    }

    public async Task SendFileAsync(long chatId, TelegramFileUpload upload, CancellationToken cancellationToken)
    {
        var field = upload.AsPhoto ? "photo" : "document";
        await using var file = new FileStream(upload.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var content = new MultipartFormDataContent
        {
            { new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id" }
        };
        if (upload.Caption is { Length: > 0 } caption) content.Add(new StringContent(caption), "caption");
        // Telegram would otherwise turn an image sent as document into a photo; the original must stay as sent.
        if (!upload.AsPhoto) content.Add(new StringContent("true"), "disable_content_type_detection");
        var stream = new StreamContent(file);
        stream.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(upload.MediaType);
        content.Add(stream, field, upload.FileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, MethodUrl(upload.AsPhoto ? "sendPhoto" : "sendDocument"))
        {
            Content = content
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await ThrowIfRateLimitedAsync(response, cancellationToken);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<TelegramEnvelope<JsonElement>>(
            JsonOptions, cancellationToken);
        if (envelope is not { Ok: true }) throw new JsonException("Resposta inválida da API do Telegram.");
    }

    private static async Task ThrowIfRateLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.TooManyRequests) return;
        var delay = response.Headers.RetryAfter?.Delta;
        if (response.Headers.RetryAfter?.Date is { } retryAt)
            delay = retryAt - DateTimeOffset.UtcNow;
        try
        {
            if (response.Content is not null)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("parameters", out var parameters) &&
                    parameters.TryGetProperty("retry_after", out var seconds) &&
                    seconds.TryGetInt32(out var value))
                    delay = TimeSpan.FromSeconds(value);
            }
        }
        catch (JsonException) { /* Back off even if Telegram returned an invalid body. */ }
        throw new TelegramRateLimitException(delay);
    }

    public Task EditApprovalAsync(long chatId, long messageId, string text, CancellationToken cancellationToken)
        => SendControlAsync("editMessageText", new { chat_id = chatId, message_id = messageId, text,
            reply_markup = new TelegramInlineKeyboard([]) }, cancellationToken);

    public Task AnswerCallbackAsync(string callbackId, string text, CancellationToken cancellationToken)
        => SendControlAsync("answerCallbackQuery", new { callback_query_id = callbackId, text }, cancellationToken);

    private async Task SendControlAsync(string method, object body, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(MethodUrl(method), body, cancellationToken);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<TelegramEnvelope<JsonElement>>(
            JsonOptions, cancellationToken);
        if (envelope is not { Ok: true }) throw new JsonException("Resposta inválida da API do Telegram.");
    }

    public async Task SendChatActionAsync(long chatId, string action, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, MethodUrl("sendChatAction"))
        {
            Content = JsonContent.Create(new { chat_id = chatId, action })
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<long> DownloadFileAsync(string fileId, Stream destination, long maxBytes,
        CancellationToken cancellationToken)
    {
        TelegramFile file;
        using (var response = await httpClient.PostAsJsonAsync(MethodUrl("getFile"), new { file_id = fileId },
                   cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var envelope = await response.Content.ReadFromJsonAsync<TelegramEnvelope<TelegramFile>>(
                JsonOptions, cancellationToken);
            if (envelope is not { Ok: true, Result.FilePath: not null }) throw new JsonException("Resposta inválida da API do Telegram.");
            file = envelope.Result;
        }
        if (file.FileSize > maxBytes) throw new TelegramFileTooLargeException();

        // The download URL carries the token: it is built here only, and never logged or put in an exception.
        using var request = new HttpRequestMessage(HttpMethod.Get, FileUrl(file.FilePath!));
        using var download = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        download.EnsureSuccessStatusCode();
        if (download.Content.Headers.ContentLength > maxBytes) throw new TelegramFileTooLargeException();

        await using var source = await download.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new TelegramFileTooLargeException();
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return total;
    }

    // file_path comes from Telegram, but it still only becomes path segments of the file endpoint.
    private Uri FileUrl(string filePath)
    {
        var segments = filePath.Split('/');
        if (filePath.Length == 0 || segments.Any(segment => segment is "" or "." or ".." ||
                segment.IndexOfAny(['\\', '?', '#', '%']) >= 0))
            throw new JsonException("Caminho de arquivo inválido na resposta do Telegram.");
        return new Uri($"https://api.telegram.org/file/bot{Token()}/{string.Join('/', segments.Select(Uri.EscapeDataString))}");
    }

    private Uri MethodUrl(string method) => new($"https://api.telegram.org/bot{Token()}/{method}");

    private string Token()
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Telegram__BotToken não configurado.");
        }

        return token;
    }

    private sealed record TelegramFile(
        [property: JsonPropertyName("file_id")] string FileId,
        [property: JsonPropertyName("file_size")] long? FileSize,
        [property: JsonPropertyName("file_path")] string? FilePath);

    private sealed record TelegramEnvelope<T>(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("result")] T? Result);
}

internal sealed class TelegramRateLimitException(TimeSpan? retryAfter)
    : HttpRequestException("Telegram limitou a taxa de mensagens.", null, HttpStatusCode.TooManyRequests)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed class TelegramMarkupException()
    : HttpRequestException("Telegram recusou a formatação da mensagem.", null, HttpStatusCode.BadRequest);
