using System.Globalization;
using Microsoft.Extensions.Options;

namespace Dante.Worker.Telegram;

public sealed class TelegramUserAuthorizer
{
    private readonly HashSet<long> allowedUserIds = [];

    public TelegramUserAuthorizer(IOptions<TelegramOptions> options)
    {
        var configuredIds = options.Value.AllowedUserIds;
        if (string.IsNullOrWhiteSpace(configuredIds))
        {
            return;
        }

        foreach (var entry in configuredIds.Split(','))
        {
            if (!long.TryParse(entry.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id <= 0)
            {
                allowedUserIds.Clear();
                return;
            }

            allowedUserIds.Add(id);
        }
    }

    public bool IsAuthorized(TelegramUser? user) =>
        user is not null && allowedUserIds.Contains(user.Id);
}
