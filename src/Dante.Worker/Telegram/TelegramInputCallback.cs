using Dante.Worker.Sessions;

namespace Dante.Worker.Telegram;

// The answer text never travels in callback data; it is recovered from the registry's pending question.
internal sealed record TelegramInputCallback(string SessionId, string TurnId, string RequestId, int Option)
{
    internal static TelegramInlineKeyboard? Keyboard(UserInputRequestedEvent input)
    {
        if (input.Questions.Count != 1 || input.Questions[0].Options is not { Count: > 0 and <= 10 } options ||
            options.Any(option => string.IsNullOrWhiteSpace(option) || option.Length > 64 ||
                option.Contains('\n') || option.Contains('\r'))) return null;
        var buttons = options.Select((option, index) => new TelegramInlineButton(option,
            $"in:{input.SessionId}:{input.TurnId}:{input.RequestId}:{index}")).ToArray();
        if (buttons.Any(button => button.CallbackData.Length > 64)) return null;
        return new(buttons.Select(button => (IReadOnlyList<TelegramInlineButton>)new[] { button }).ToArray());
    }

    internal static bool TryParse(string? data, out TelegramInputCallback action)
    {
        action = null!;
        if (data is null || data.Length > 64) return false;
        var parts = data.Split(':');
        if (parts.Length != 5 || parts[0] != "in" ||
            !ValidId(parts[1], 'S') || !ValidId(parts[2], 'T') || !ValidId(parts[3], 'R') ||
            parts[4].Length != 1 || parts[4][0] is < '0' or > '9') return false;
        action = new(parts[1], parts[2], parts[3], parts[4][0] - '0');
        return true;
    }

    private static bool ValidId(string id, char prefix) => id.Length is >= 7 and <= 12 &&
        id[0] == prefix && id.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
}
