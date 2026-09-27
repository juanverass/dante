using Dante.Worker.Telegram;
using Microsoft.Extensions.Options;

namespace Dante.Tests;

public sealed class TelegramUserAuthorizerTests
{
    [Theory]
    [InlineData("123, 456", 123L, true)]
    [InlineData("123, 456", 456L, true)]
    [InlineData("123, 456", 789L, false)]
    [InlineData("", 123L, false)]
    [InlineData("123,", 123L, false)]
    [InlineData("123,invalid", 123L, false)]
    [InlineData("-123", 123L, false)]
    public void AuthorizesOnlyListedTelegramUserIds(string allowedUserIds, long userId, bool expected)
    {
        var authorizer = new TelegramUserAuthorizer(
            Options.Create(new TelegramOptions { AllowedUserIds = allowedUserIds }));

        Assert.Equal(expected, authorizer.IsAuthorized(new TelegramUser(userId)));
        Assert.False(authorizer.IsAuthorized(null));
    }
}
