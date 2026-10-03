using System.Globalization;
using Dante.Worker.Usage;

namespace Dante.Worker.Telegram;

// Text of /uso (#116). The remaining time comes from the provider's reset and the given clock; a reset already past is
// not a confirmed renewal, and a missing metric is shown as unavailable with its reason.
internal static class UsageReportFormatter
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    public static string Format(UsageReport report, DateTimeOffset now)
    {
        var agent = report.Agent.ToString();
        var lines = new List<string>
        {
            $"Uso — {agent}",
            $"Janela de sessão{Suffix(report.Session.Window?.Duration)}: {Used(report.Session)}",
            $"Semana: {Used(report.Weekly)}",
            "Janela de sessão renova em: " + (report.Session.Window is not { } session
                ? "indisponível (sem janela de sessão)"
                : session.ResetsAt is not { } resetsAt
                    ? $"indisponível (o {agent} não informou o horário)"
                    : resetsAt <= now
                        ? "o horário informado já passou; consulte de novo"
                        : Duration(resetsAt - now))
        };
        if (report.Additional.Count > 0)
        {
            lines.Add("Outros limites (não somados aos acima):");
            lines.AddRange(report.Additional.Select(window =>
                $"- {window.Label}{Suffix(window.Duration)}: {Percent(window.UsedPercent)} utilizado" +
                (window.ResetsAt is { } resetsAt && resetsAt > now ? $"; renova em {Duration(resetsAt - now)}" : "")));
        }

        if (report.Note is not null) lines.Add(report.Note);
        var age = now - report.QueriedAt;
        lines.Add((age < TimeSpan.FromMinutes(1) ? "Consultado agora" : $"Consultado há {Duration(age)}") +
                  $" ({report.QueriedAt.UtcDateTime:HH:mm} UTC)");
        lines.Add($"Cota da conta autenticada no {agent} deste host, inclusive uso fora do D.A.N.T.E.");
        return string.Join('\n', lines);
    }

    // 2h 13min, 3d 4h, 45min; under a minute is not rounded to zero.
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1)) return "menos de 1 min";
        var days = (int)duration.TotalDays;
        if (days > 0) return duration.Hours > 0 ? $"{days}d {duration.Hours}h" : $"{days}d";
        if (duration.Hours > 0) return duration.Minutes > 0 ? $"{duration.Hours}h {duration.Minutes}min" : $"{duration.Hours}h";
        return $"{duration.Minutes}min";
    }

    private static string Used(QuotaMetric metric) => metric.Window is { } window
        ? $"{Percent(window.UsedPercent)} do limite utilizado"
        : $"indisponível ({metric.UnavailableReason})";

    private static string Percent(decimal value) => value.ToString("0.#", Portuguese) + "%";

    private static string Suffix(TimeSpan? duration) => duration is { } value ? $" ({Duration(value)})" : "";
}
