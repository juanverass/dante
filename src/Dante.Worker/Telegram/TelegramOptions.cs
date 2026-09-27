namespace Dante.Worker.Telegram;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    public string AllowedUserIds { get; set; } = string.Empty;
}
