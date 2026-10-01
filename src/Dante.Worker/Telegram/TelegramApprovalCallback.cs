using Dante.Worker.Sessions;

namespace Dante.Worker.Telegram;

internal sealed record TelegramApprovalCallback(string SessionId, string TurnId, string RequestId,
    AgentApprovalDecision Decision)
{
    public static string Encode(ApprovalRequestedEvent request, AgentApprovalDecision decision) =>
        $"ap:{request.SessionId}:{request.TurnId}:{request.RequestId}:{(int)decision}";

    public static bool TryParse(string? data, out TelegramApprovalCallback action)
    {
        action = null!;
        if (data is null || data.Length > 64) return false;
        var parts = data.Split(':');
        if (parts.Length != 5 || parts[0] != "ap" ||
            !ValidId(parts[1], 'S') || !ValidId(parts[2], 'T') || !ValidId(parts[3], 'R') ||
            !int.TryParse(parts[4], out var value) || !Enum.IsDefined(typeof(AgentApprovalDecision), value))
            return false;
        action = new(parts[1], parts[2], parts[3], (AgentApprovalDecision)value);
        return true;
    }

    private static bool ValidId(string id, char prefix) => id.Length is >= 7 and <= 12 &&
        id[0] == prefix && id.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    public static string DecisionText(AgentApprovalDecision decision) => decision switch
    {
        AgentApprovalDecision.ApproveOnce => "✅ Aprovado uma vez",
        AgentApprovalDecision.ApproveForSession => "✅ Aprovado na sessão",
        _ => "❌ Negado"
    };

    public static TelegramInlineKeyboard Keyboard(ApprovalRequestedEvent request)
    {
        List<TelegramInlineButton> approve = [new("Aprovar uma vez", Encode(request, AgentApprovalDecision.ApproveOnce))];
        if (request.CanApproveForSession)
            approve.Add(new("Aprovar na sessão", Encode(request, AgentApprovalDecision.ApproveForSession)));
        return new([approve, new[] { new TelegramInlineButton("Negar", Encode(request, AgentApprovalDecision.Deny)) }]);
    }
}
