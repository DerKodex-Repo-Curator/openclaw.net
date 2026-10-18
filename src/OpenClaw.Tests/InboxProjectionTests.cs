using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Inbox;
using OpenClaw.Gateway.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B7 projection (design §10.2): run events and MCP transitions become items, and the journals can
/// reproduce the same items afterwards.
/// </summary>
public sealed class InboxProjectionTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "openclaw-inbox-projection", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 18, 2, 15, 0, TimeSpan.Zero);

    private static readonly Guid Invocation = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task T_B7_1_An_approval_request_opens_an_approval_item()
    {
        var (projector, store) = Create();

        await projector.ProjectAsync(
            Event(1, MetaRunEventKinds.ApprovalRequested, "step-refund", detail: "map_0123456789ab"), Ct);

        var item = Assert.Single(await store.SnapshotAsync(Ct));
        Assert.Equal(InboxKinds.Approval, item.Kind);
        Assert.Equal(InboxStates.Open, item.State);
        Assert.Equal(Invocation, item.Subject.InvocationId);
        Assert.Equal("map_0123456789ab", item.Subject.ApprovalId);
        Assert.Equal("step-refund", item.Subject.StepId);
        Assert.Equal(InboxSeverities.Warning, item.Severity);
    }

    [Fact]
    public async Task T_B7_2_A_denied_approval_closes_the_item_without_anyone_acking_it()
    {
        var (projector, store) = Create();

        await projector.ProjectAsync(Event(1, MetaRunEventKinds.ApprovalRequested, "step-1", "map_abc"), Ct);
        await projector.ProjectAsync(Event(2, MetaRunEventKinds.ApprovalDecided, "step-1", "denied"), Ct);

        var item = Assert.Single(await store.SnapshotAsync(Ct));
        Assert.Equal(InboxStates.Resolved, item.State);
        Assert.Equal(InboxResolutionCodes.Rejected, item.ResolutionCode);
        Assert.NotNull(item.ResolvedAtUtc);
    }

    [Fact]
    public async Task T_B7_3_An_acknowledged_approval_still_closes_when_the_decision_arrives()
    {
        var (projector, store) = Create();

        await projector.ProjectAsync(Event(1, MetaRunEventKinds.ApprovalRequested, "step-1", "map_abc"), Ct);
        var item = Assert.Single(await store.SnapshotAsync(Ct));
        await store.AcknowledgeAsync(item.Id, Ct);

        await projector.ProjectAsync(Event(2, MetaRunEventKinds.ApprovalDecided, "step-1", "approved"), Ct);

        var closed = await store.GetAsync(item.Id, Ct);
        Assert.Equal(InboxStates.Resolved, closed!.State);
        Assert.Equal(InboxResolutionCodes.Approved, closed.ResolutionCode);
    }

    [Fact]
    public async Task A_paused_run_opens_one_item_and_the_resume_closes_it()
    {
        var (projector, store) = Create();

        await projector.ProjectAsync(Event(1, MetaRunEventKinds.StepPaused, "step-1"), Ct);
        await projector.ProjectAsync(Event(2, MetaRunEventKinds.StepPaused, "step-2"), Ct);

        // Two pauses of the same run are one todo: the operator has one run to unblock.
        var item = Assert.Single(await store.SnapshotAsync(Ct));
        Assert.Equal(InboxKinds.RunPaused, item.Kind);

        await projector.ProjectAsync(Event(3, MetaRunEventKinds.Resumed, "step-2"), Ct);

        Assert.Equal(InboxStates.Resolved, (await store.GetAsync(item.Id, Ct))!.State);
        Assert.Equal(InboxResolutionCodes.AutoClosed, (await store.GetAsync(item.Id, Ct))!.ResolutionCode);
    }

    [Fact]
    public async Task A_failed_run_opens_a_critical_item_and_clears_the_pause_row()
    {
        var (projector, store) = Create();

        await projector.ProjectAsync(Event(1, MetaRunEventKinds.StepPaused, "step-1"), Ct);
        await projector.ProjectAsync(Event(2, MetaRunEventKinds.Failed, "step-1"), Ct);

        var items = await store.SnapshotAsync(Ct);
        var failed = Assert.Single(items, item => item.Kind == InboxKinds.RunFailed);
        Assert.Equal(InboxSeverities.Critical, failed.Severity);
        Assert.Equal(InboxStates.Open, failed.State);

        var paused = Assert.Single(items, item => item.Kind == InboxKinds.RunPaused);
        Assert.Equal(InboxStates.Resolved, paused.State);
        Assert.Equal(InboxResolutionCodes.Failed, paused.ResolutionCode);
    }

    [Fact]
    public async Task An_uncertain_run_is_a_warning_not_a_confirmed_failure()
    {
        var (projector, store) = Create();

        await projector.ProjectAsync(Event(1, MetaRunEventKinds.Uncertain), Ct);

        var item = Assert.Single(await store.SnapshotAsync(Ct));
        Assert.Equal(InboxKinds.RunFailed, item.Kind);
        Assert.Equal(InboxSeverities.Warning, item.Severity);
    }

    [Fact]
    public async Task An_approval_request_closes_the_generic_pause_row_for_the_same_run()
    {
        var (projector, store) = Create();

        await projector.ProjectAsync(Event(1, MetaRunEventKinds.StepPaused, "step-1"), Ct);
        await projector.ProjectAsync(Event(2, MetaRunEventKinds.ApprovalRequested, "step-1", "map_abc"), Ct);

        var items = await store.SnapshotAsync(Ct);
        Assert.Single(items, item => item.Kind == InboxKinds.Approval && item.State == InboxStates.Open);
        Assert.Equal(
            InboxStates.Resolved,
            Assert.Single(items, item => item.Kind == InboxKinds.RunPaused).State);
    }

    [Fact]
    public async Task T_B7_7_An_mcp_server_going_down_opens_an_item_that_recovers_itself()
    {
        var (projector, store) = Create();

        await projector.ProjectMcpAsync(Transition(McpServerHealthState.Healthy, McpServerHealthState.Down), Ct);

        var item = Assert.Single(await store.SnapshotAsync(Ct));
        Assert.Equal(InboxKinds.McpDown, item.Kind);
        Assert.Equal("alpha", item.Subject.ServerId);
        Assert.Equal(InboxSeverities.Critical, item.Severity);

        await projector.ProjectMcpAsync(Transition(McpServerHealthState.Down, McpServerHealthState.Healthy), Ct);

        Assert.Equal(InboxStates.Resolved, (await store.GetAsync(item.Id, Ct))!.State);
        Assert.Equal(InboxResolutionCodes.Recovered, (await store.GetAsync(item.Id, Ct))!.ResolutionCode);
    }

    [Fact]
    public async Task A_degraded_server_is_not_an_inbox_item()
    {
        var (projector, store) = Create();

        await projector.ProjectMcpAsync(Transition(McpServerHealthState.Healthy, McpServerHealthState.Degraded), Ct);

        Assert.Empty(await store.SnapshotAsync(Ct));
    }

    [Fact]
    public async Task T_B7_5_A_rebuild_from_the_journals_reproduces_the_live_item_set()
    {
        var (projector, store, log) = CreateWithLog();

        // Live projection of the same events the journal will hold.
        await projector.ProjectAsync(Event(1, MetaRunEventKinds.StepPaused, "step-1"), Ct);
        await projector.ProjectAsync(Event(2, MetaRunEventKinds.ApprovalRequested, "step-1", "map_abc"), Ct);
        var live = await store.SnapshotAsync(Ct);
        Assert.Equal(2, live.Count);

        await log.AppendAsync(
            Invocation,
            [
                new MetaRunEventDraft(MetaRunEventKinds.StepPaused, "step-1", "AwaitingInput", "pause"),
                new MetaRunEventDraft(MetaRunEventKinds.ApprovalRequested, "step-1", "AwaitingInput", "map_abc")
            ],
            Start,
            Ct);

        var result = await projector.RebuildAsync(Ct);

        Assert.Equal(2, result.EventsRead);
        Assert.Equal(live.Count, result.Items);
        Assert.Equal(
            live.OrderBy(item => item.Id).Select(item => $"{item.Id}|{item.Kind}|{item.State}|{item.Subject.ApprovalId}"),
            (await store.SnapshotAsync(Ct)).OrderBy(item => item.Id)
                .Select(item => $"{item.Id}|{item.Kind}|{item.State}|{item.Subject.ApprovalId}"));
    }

    [Fact]
    public async Task A_rebuild_drops_items_the_journals_no_longer_support()
    {
        var (projector, store, _) = CreateWithLog();

        await projector.ProjectAsync(Event(1, MetaRunEventKinds.StepPaused, "step-1"), Ct);
        Assert.Single(await store.SnapshotAsync(Ct));

        // No journals at all: the projection has nothing to stand on.
        var result = await projector.RebuildAsync(Ct);

        Assert.Equal(0, result.Items);
        Assert.Empty(await store.SnapshotAsync(Ct));
    }

    private (InboxProjector Projector, InboxStore Store) Create()
    {
        var (projector, store, _) = CreateWithLog();
        return (projector, store);
    }

    private (InboxProjector Projector, InboxStore Store, MetaRunEventLog Log) CreateWithLog()
    {
        var time = new FixedTime(Start);
        var stream = new InboxStream();
        var store = new InboxStore(new InboxConfig(), root, stream, time);
        var queue = new InboxProjectionQueue();
        var log = new MetaRunEventLog(root);
        var projector = new InboxProjector(
            store, queue, new InboxConfig(), null, null, log,
            NullLogger<InboxProjector>.Instance, time);

        return (projector, store, log);
    }

    private static MetaRunEvent Event(
        long seq,
        string kind,
        string? stepId = null,
        string? detail = null)
        => new(Invocation, seq, Start, kind, stepId, "AwaitingInput", detail);

    private static McpHealthTransition Transition(McpServerHealthState previous, McpServerHealthState next)
        => new(
            new McpServerHealth
            {
                ServerId = "alpha",
                State = next,
                LastProbeAtUtc = Start,
                LastErrorCode = next == McpServerHealthState.Down ? McpProbeErrorCodes.Timeout : null
            },
            previous,
            StateChanged: previous != next,
            ToolsChanged: false);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
