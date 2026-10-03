using Dante.Worker.Agents;
using Dante.Worker.Telegram;
using Dante.Worker.Usage;

namespace Dante.Tests;

// Text of /uso (#116) with a controlled clock: the remaining time comes from the provider's reset, never negative.
public sealed class UsageReportFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 14, 5, 0, TimeSpan.Zero);

    [Fact]
    public void ShowsTheThreeRequestedFieldsAndTheQueryTime()
    {
        var report = new UsageReport(AgentKind.Codex, Now,
            QuotaMetric.Of(new QuotaWindow(37, Now + new TimeSpan(2, 13, 40), TimeSpan.FromHours(5))),
            QuotaMetric.Of(new QuotaWindow(62, Now + TimeSpan.FromDays(3), TimeSpan.FromDays(7))), []);

        Assert.Equal("""
            Uso — Codex
            Janela de sessão (5h): 37% do limite utilizado
            Semana: 62% do limite utilizado
            Janela de sessão renova em: 2h 13min
            Consultado agora (14:05 UTC)
            Cota da conta autenticada no Codex deste host, inclusive uso fora do D.A.N.T.E.
            """.ReplaceLineEndings("\n"), UsageReportFormatter.Format(report, Now + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void MissingMetricsAreUnavailableAndAPastResetIsNotARenewal()
    {
        var unavailable = new UsageReport(AgentKind.Codex, Now,
            QuotaMetric.Unavailable("o Codex não informou uma janela de sessão"),
            QuotaMetric.Unavailable("o Codex não informou uma janela semanal"), []);
        var text = UsageReportFormatter.Format(unavailable, Now);
        Assert.Contains("Janela de sessão: indisponível (o Codex não informou uma janela de sessão)", text);
        Assert.Contains("Semana: indisponível (o Codex não informou uma janela semanal)", text);
        Assert.Contains("Janela de sessão renova em: indisponível (sem janela de sessão)", text);
        Assert.DoesNotContain("0%", text);

        var noReset = unavailable with { Session = QuotaMetric.Of(new QuotaWindow(10, null, null)) };
        Assert.Contains("renova em: indisponível (o Codex não informou o horário)", UsageReportFormatter.Format(noReset, Now));

        var past = unavailable with { Session = QuotaMetric.Of(new QuotaWindow(100, Now - TimeSpan.FromMinutes(3), null)) };
        text = UsageReportFormatter.Format(past, Now);
        Assert.Contains("renova em: o horário informado já passou; consulte de novo", text);
        Assert.DoesNotContain("-", text.Split('\n')[3]);
    }

    [Fact]
    public void OtherLimitsAreListedApartWithTheirOwnReset()
    {
        var report = new UsageReport(AgentKind.Codex, Now,
            QuotaMetric.Of(new QuotaWindow(12.5m, Now + TimeSpan.FromSeconds(30), TimeSpan.FromHours(5))),
            QuotaMetric.Unavailable("o Codex não informou uma janela semanal"),
            [new QuotaWindow(42, Now + TimeSpan.FromMinutes(45), TimeSpan.FromHours(1), "Codex Other"),
             new QuotaWindow(3, null, null, "Codex")]);

        var text = UsageReportFormatter.Format(report, Now);

        Assert.Contains("Janela de sessão (5h): 12,5% do limite utilizado", text);
        Assert.Contains("renova em: menos de 1 min", text);
        Assert.Contains("Outros limites (não somados aos acima):\n- Codex Other (1h): 42% utilizado; renova em 45min\n" +
                        "- Codex: 3% utilizado", text);
    }

    [Fact]
    public void OldQueryDoesNotLookCurrent()
    {
        var report = new UsageReport(AgentKind.Codex, Now, QuotaMetric.Unavailable("x"), QuotaMetric.Unavailable("y"), []);

        Assert.Contains("Consultado há 5min (14:05 UTC)", UsageReportFormatter.Format(report, Now + TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData(0, 0, 30, "menos de 1 min")]
    [InlineData(0, 45, 0, "45min")]
    [InlineData(2, 0, 0, "2h")]
    [InlineData(2, 13, 59, "2h 13min")]
    [InlineData(76, 0, 0, "3d 4h")]
    [InlineData(168, 0, 0, "7d")]
    public void DurationsUseHumanUnits(int hours, int minutes, int seconds, string expected) =>
        Assert.Equal(expected, UsageReportFormatter.Duration(new TimeSpan(hours, minutes, seconds)));
}
