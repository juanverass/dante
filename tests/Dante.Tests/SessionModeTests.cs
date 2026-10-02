using Dante.Worker.Agents;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dante.Tests;

public sealed class SessionModeTests
{
    private const long Owner = 42;
    private readonly FakeSessionDriverFactory drivers = new();
    private static readonly JobExecutionContext Context = JobExecutionContext.General("/tmp/modes");
    private SessionRegistry Registry() => new(drivers, NullLogger<SessionRegistry>.Instance);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEndedSessionDoesNotKeepAnOverrideThatCanNeverBeApplied(bool failure)
    {
        await using var registry = Registry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Context));
        await registry.ChangeModeAsync(Owner, null, AgentPermissionProfile.Plan);
        if (failure)
        {
            drivers.Created.Single().TurnFailure = new AgentProtocolException("no settings confirmation");
            await registry.SubmitAsync(Owner, null, "work");
        }
        else await registry.CloseAsync(Owner, null);
        var ended = registry.List(Owner).Single();
        Assert.Null(ended.PendingProfile);
        Assert.Equal(AgentPermissionProfile.Manual, ended.Profile);
    }

    [Fact]
    public async Task ADeferredUpstreamRefusalRetainsTheThreadAndPreviousEffectiveMode()
    {
        await using var registry = Registry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Context));
        await registry.ChangeModeAsync(Owner, null, AgentPermissionProfile.Auto);
        var driver = drivers.Created.Single();
        driver.TurnFailure = new AgentModeRejectedException("rejected mode");
        var submit = await registry.SubmitAsync(Owner, null, "work");
        Assert.Equal(SubmitOutcome.Rejected, submit.Outcome);
        Assert.Equal(AgentPermissionProfile.Manual, registry.GetActive(Owner)!.Profile);
        Assert.Null(registry.GetActive(Owner)!.PendingProfile);
        Assert.Equal(AgentSessionState.Idle, registry.GetActive(Owner)!.State);
        Assert.False(driver.Disposed);
    }

    [Fact]
    public async Task ClaudeUpdatesOnlyAfterConfirmationAndSerializesANewTurn()
    {
        await using var registry = Registry();
        var original = (await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Context,
            ModelSelection: new AgentModelSelection("opus", "high")))).Session!;
        var driver = drivers.Created.Single();
        driver.ModeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var change = registry.ChangeModeAsync(Owner, original.Id, AgentPermissionProfile.Plan);
        Assert.Equal(AgentPermissionProfile.Manual, registry.GetActive(Owner)!.Profile);
        var turn = registry.SubmitAsync(Owner, null, "continue");
        Assert.DoesNotContain("turn:continue", driver.Calls);
        driver.ModeGate.SetResult();
        Assert.True((await change).Accepted);
        await turn;
        var updated = registry.GetActive(Owner)!;
        Assert.Equal(AgentPermissionProfile.Plan, updated.Profile);
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(original.Context, updated.Context);
        Assert.Equal(original.ModelSelection, updated.ModelSelection);
        Assert.Single(drivers.Created);
    }

    [Fact]
    public async Task CodexKeepsTheEffectiveModeUntilTheNextTurnConfirmsTheOverride()
    {
        await using var registry = Registry();
        var original = (await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Context,
            Profile: AgentPermissionProfile.Auto))).Session!;
        var changed = await registry.ChangeModeAsync(Owner, null, AgentPermissionProfile.Manual);
        Assert.True(changed.Accepted);
        Assert.Equal(AgentPermissionProfile.Auto, changed.Session!.Profile);
        Assert.Equal(AgentPermissionProfile.Manual, changed.Session.PendingProfile);
        await registry.SubmitAsync(Owner, null, "continue");
        await Eventually(() => registry.GetActive(Owner)!.Profile == AgentPermissionProfile.Manual);
        Assert.Null(registry.GetActive(Owner)!.PendingProfile);
        Assert.Equal(original.Id, registry.GetActive(Owner)!.Id);
        Assert.Single(drivers.Created);
    }

    [Fact]
    public async Task SelectingTheCurrentModeCancelsACodexPendingOverride()
    {
        await using var registry = Registry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Context));
        await registry.ChangeModeAsync(Owner, null, AgentPermissionProfile.Plan);
        var cancelled = await registry.ChangeModeAsync(Owner, null, AgentPermissionProfile.Manual);
        Assert.True(cancelled.Accepted);
        Assert.Null(cancelled.Session!.PendingProfile);
        Assert.Equal(AgentPermissionProfile.Manual, cancelled.Session.Profile);
    }

    [Fact]
    public async Task UpstreamRefusalKeepsThePreviousModeAndSessionUsable()
    {
        drivers.Configure = driver => driver.ModeFailure = new AgentProtocolException("rejected");
        await using var registry = Registry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Context));
        var change = await registry.ChangeModeAsync(Owner, null, AgentPermissionProfile.Auto);
        Assert.False(change.Accepted);
        Assert.Equal(AgentPermissionProfile.Manual, change.Session!.Profile);
        Assert.Equal(AgentSessionState.Idle, change.Session.State);
        Assert.Null(change.Session.PendingProfile);
        Assert.Contains("modo anterior", change.Error);
        Assert.False(drivers.Created.Single().Disposed);
    }

    [Fact]
    public async Task MissingConfirmationClosesTheUncertainProcessWithoutReportingTheRequestedMode()
    {
        drivers.Configure = driver => driver.ModeFailure = new AgentModeUnconfirmedException("missing mode");
        await using var registry = Registry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Context));
        var change = await registry.ChangeModeAsync(Owner, null, AgentPermissionProfile.Auto);
        Assert.False(change.Accepted);
        Assert.Equal(AgentPermissionProfile.Manual, change.Session!.Profile);
        Assert.Equal(AgentSessionState.Failed, change.Session.State);
        Assert.True(drivers.Created.Single().Disposed);
    }

    [Fact]
    public async Task UnsupportedDriverAndAnotherOwnerNeverReachTheModeOperation()
    {
        drivers.Configure = driver => driver.Capabilities = driver.Capabilities with { ModeSwitch = AgentModeSwitch.Unsupported };
        await using var registry = Registry();
        var session = (await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Context))).Session!;
        Assert.False((await registry.ChangeModeAsync(7, session.Id, AgentPermissionProfile.Auto)).Accepted);
        var unsupported = await registry.ChangeModeAsync(Owner, null, AgentPermissionProfile.Auto);
        Assert.False(unsupported.Accepted);
        Assert.Contains("/session start claude auto", unsupported.Error);
        Assert.Equal(["start"], drivers.Created.Single().Calls);
    }

    [Theory]
    [InlineData(false, AgentPermissionProfile.Manual, AgentPermissionProfile.Auto)]
    [InlineData(false, AgentPermissionProfile.Auto, AgentPermissionProfile.Manual)]
    [InlineData(false, AgentPermissionProfile.Manual, AgentPermissionProfile.Plan)]
    [InlineData(false, AgentPermissionProfile.Plan, AgentPermissionProfile.Manual)]
    [InlineData(true, AgentPermissionProfile.Manual, AgentPermissionProfile.Auto)]
    [InlineData(true, AgentPermissionProfile.Plan, AgentPermissionProfile.Manual)]
    public async Task ActiveTurnsAndPendingHumanRequestsAreRefusedWithoutResolvingThem(bool approval,
        AgentPermissionProfile previous, AgentPermissionProfile requested)
    {
        await using var registry = Registry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Codex, Context, Profile: previous));
        await registry.SubmitAsync(Owner, null, "work");
        var driver = drivers.Created.Single();
        if (approval) driver.Emit(new ApprovalRequestedEvent("up-request", AgentToolKind.Command, "git"));
        else driver.Emit(new UserInputRequestedEvent("up-request", [new AgentQuestion("q", "Qual?", ["a", "b"])]));
        await Eventually(() => registry.GetActive(Owner)!.PendingRequestIds.Count == 1);
        var pending = registry.GetActive(Owner)!.PendingRequestIds.ToArray();
        var result = await registry.ChangeModeAsync(Owner, null, requested);
        Assert.False(result.Accepted);
        Assert.Equal(previous, registry.GetActive(Owner)!.Profile);
        Assert.Equal(pending, registry.GetActive(Owner)!.PendingRequestIds);
        Assert.Empty(driver.Responses);
        Assert.DoesNotContain(driver.Calls, call => call.StartsWith("mode:"));
    }

    [Fact]
    public async Task RunningTurnWithoutHumanRequestIsAlsoRefused()
    {
        await using var registry = Registry();
        await registry.StartAsync(new SessionStartRequest(Owner, AgentKind.Claude, Context));
        await registry.SubmitAsync(Owner, null, "work");
        Assert.False((await registry.ChangeModeAsync(Owner, null, AgentPermissionProfile.Plan)).Accepted);
        Assert.Equal(["start", "turn:work"], drivers.Created.Single().Calls);
    }

    private static async Task Eventually(Func<bool> predicate)
    {
        for (var i = 0; i < 200 && !predicate(); i++) await Task.Delay(10);
        Assert.True(predicate());
    }
}
