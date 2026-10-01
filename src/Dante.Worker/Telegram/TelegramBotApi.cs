using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Dante.Worker.Telegram;

public sealed class TelegramBotApi(HttpClient httpClient, IOptions<TelegramOptions> options) : ITelegramBotApi
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
            Content = JsonContent.Create(new { chat_id = chatId, text, reply_markup = keyboard, parse_mode = parseMode })
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
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

    private Uri MethodUrl(string method)
    {
        var token = options.Value.BotToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Telegram__BotToken não configurado.");
        }

        return new Uri($"https://api.telegram.org/bot{token}/{method}");
    }

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
