using OpenClaw.Core.Inbox;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Inbox;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B7 storage and projection mechanics: dedupe, keyset paging, TTL, snooze and rebuild.
/// </summary>
public sealed class InboxStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "openclaw-inbox-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 18, 2, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task One_resource_is_one_active_item_and_severity_only_goes_up()
    {
        var (store, _) = Create();

        var (first, created) = await store.OpenAsync(Request(InboxKinds.RunPaused, "run-1", severity: InboxSeverities.Info));
        Assert.True(created);

        var (second, createdAgain) = await store.OpenAsync(
            Request(InboxKinds.RunPaused, "run-1", severity: InboxSeverities.Critical));

        Assert.False(createdAgain);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(InboxSeverities.Critical, second.Severity);

        var (third, _) = await store.OpenAsync(Request(InboxKinds.RunPaused, "run-1", severity: InboxSeverities.Info));
        Assert.Equal(InboxSeverities.Critical, third.Severity);

        Assert.Single(await store.SnapshotAsync(Ct));
    }

    [Fact]
    public async Task A_closed_resource_reopens_the_same_item_instead_of_minting_a_new_id()
    {
        var (store, _) = Create();

        var (item, _) = await store.OpenAsync(Request(InboxKinds.McpDown, "alpha"));
        await store.ResolveByResourceAsync(InboxKinds.McpDown, "alpha", InboxResolutionCodes.Recovered, Ct);

        var (reopened, created) = await store.OpenAsync(Request(InboxKinds.McpDown, "alpha"));

        Assert.True(created);
        Assert.Equal(item.Id, reopened.Id);
        Assert.Equal(InboxStates.Open, reopened.State);
        Assert.Null(reopened.ResolutionCode);
        Assert.Null(reopened.ResolvedAtUtc);
    }

    [Fact]
    public async Task List_is_a_keyset_page_newest_first_with_counts()
    {
        var (store, time) = Create();

        for (var index = 0; index < 5; index++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await store.OpenAsync(Request(InboxKinds.RunFailed, $"run-{index}"));
        }

        var firstPage = await store.ListAsync(state: null, kind: null, cursor: null, limit: 2, Ct);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal("run-4", firstPage.Items[0].Subject.ResourceKey);
        Assert.Equal("run-3", firstPage.Items[1].Subject.ResourceKey);
        Assert.NotNull(firstPage.NextCursor);
        Assert.Equal(5, firstPage.Counts.Open);

        var secondPage = await store.ListAsync(state: null, kind: null, firstPage.NextCursor, 2, Ct);
        Assert.Equal("run-2", secondPage.Items[0].Subject.ResourceKey);

        var activeOnly = await store.ListAsync(state: "active", kind: InboxKinds.RunFailed, cursor: null, limit: 10, Ct);
        Assert.Equal(5, activeOnly.Items.Count);

        var byKind = await store.ListAsync(state: null, kind: InboxKinds.Approval, cursor: null, limit: 10, Ct);
        Assert.Empty(byKind.Items);
    }

    [Fact]
    public async Task T_B7_4_A_snooze_wakes_on_the_sweep_exactly_once()
    {
        var (store, time) = Create();
        var (item, _) = await store.OpenAsync(Request(InboxKinds.RunPaused, "run-1"));

        var snoozed = await store.SnoozeAsync(item.Id, Start.AddHours(1), Ct);
        Assert.True(snoozed.Succeeded);
        Assert.Equal(Start.AddHours(1), snoozed.Item.SnoozeUntilUtc);

        time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, await store.SweepAsync(Ct));

        var woke = await store.GetAsync(item.Id, Ct);
        Assert.Null(woke!.SnoozeUntilUtc);
        Assert.Equal(InboxStates.Open, woke.State);

        // Second sweep: nothing left to wake.
        Assert.Equal(0, await store.SweepAsync(Ct));
    }

    [Fact]
    public async Task T_B7_10_A_past_due_item_expires()
    {
        var (store, time) = Create();
        var (item, _) = await store.OpenAsync(Request(InboxKinds.McpDown, "alpha"));
        Assert.NotNull(item.ExpiresAtUtc);

        time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await store.SweepAsync(Ct));

        var expired = await store.GetAsync(item.Id, Ct);
        Assert.Equal(InboxStates.Expired, expired!.State);
        Assert.Equal(InboxResolutionCodes.Expired, expired.ResolutionCode);

        var counts = (await store.ListAsync(null, null, null, 10, Ct)).Counts;
        Assert.Equal(0, counts.Total);
    }

    [Fact]
    public async Task T_B7_7_A_human_cannot_close_a_resource_driven_item()
    {
        var (store, _) = Create();
        var (item, _) = await store.OpenAsync(Request(InboxKinds.McpDown, "alpha"));

        var mutation = await store.ResolveAsync(item.Id, InboxResolutionCodes.Resolved, Ct);

        Assert.False(mutation.Succeeded);
        Assert.Equal(InboxErrorCodes.ResourceDriven, mutation.ErrorCode);
        Assert.Equal(InboxStates.Open, (await store.GetAsync(item.Id, Ct))!.State);
    }

    [Fact]
    public async Task A_notification_item_is_the_one_a_human_may_close()
    {
        var (store, _) = Create();
        var (item, _) = await store.OpenAsync(Request(InboxKinds.Notification, "notification:n_abc"));

        var mutation = await store.ResolveAsync(item.Id, InboxResolutionCodes.Resolved, Ct);

        Assert.True(mutation.Succeeded);
        Assert.Equal(InboxStates.Resolved, mutation.Item.State);
    }

    [Fact]
    public async Task T_B7_5_Rebuild_keeps_what_a_human_decided_and_is_field_for_field_stable()
    {
        var (store, _) = Create();
        var (paused, _) = await store.OpenAsync(Request(InboxKinds.RunPaused, "run-1"));
        var (approval, _) = await store.OpenAsync(Request(InboxKinds.Approval, "run-2"));
        await store.AcknowledgeAsync(approval.Id, Ct);

        var before = await store.SnapshotAsync(Ct);

        // The projector derives the same set from the journals; state lives in the store.
        var derived = before.Select(item => item with { State = InboxStates.Open }).ToList();
        var count = await store.RebuildAsync(derived, Ct);

        Assert.Equal(before.Count, count);
        var after = await store.SnapshotAsync(Ct);
        Assert.Equal(
            before.OrderBy(item => item.Id).Select(Describe),
            after.OrderBy(item => item.Id).Select(Describe));

        // ...and the human's acknowledgement survived the rebuild.
        Assert.Equal(InboxStates.Acknowledged, (await store.GetAsync(approval.Id, Ct))!.State);
        Assert.Equal(InboxStates.Open, (await store.GetAsync(paused.Id, Ct))!.State);
    }

    [Fact]
    public async Task Rebuild_follows_the_journals_when_they_say_an_item_closed()
    {
        var (store, _) = Create();
        var (item, _) = await store.OpenAsync(Request(InboxKinds.RunPaused, "run-1"));

        var resolved = item with
        {
            State = InboxStates.Resolved,
            ResolutionCode = InboxResolutionCodes.Completed,
            ResolvedAtUtc = Start.AddMinutes(1)
        };

        await store.RebuildAsync([resolved], Ct);

        var after = await store.GetAsync(item.Id, Ct);
        Assert.Equal(InboxStates.Resolved, after!.State);
        Assert.Equal(InboxResolutionCodes.Completed, after.ResolutionCode);
    }

    [Fact]
    public async Task The_journal_reloads_across_a_restart()
    {
        var (store, _) = Create();
        var (item, _) = await store.OpenAsync(Request(InboxKinds.Approval, "run-1"));
        await store.AcknowledgeAsync(item.Id, Ct);

        var (restarted, _) = Create();
        await restarted.LoadAsync(Ct);

        var reloaded = await restarted.GetAsync(item.Id, Ct);
        Assert.NotNull(reloaded);
        Assert.Equal(InboxStates.Acknowledged, reloaded.State);
        Assert.Equal(item.CreatedAtUtc, reloaded.CreatedAtUtc);
    }

    [Fact]
    public async Task A_damaged_journal_line_is_skipped_and_counted()
    {
        var (store, _) = Create();
        await store.OpenAsync(Request(InboxKinds.RunFailed, "run-1"));
        await File.AppendAllTextAsync(store.JournalPath, "{not-json\n", Ct);

        var (restarted, _) = Create();
        await restarted.LoadAsync(Ct);

        Assert.Equal(1, restarted.SkippedLines);
        Assert.Single(await restarted.SnapshotAsync(Ct));
    }

    private (InboxStore Store, ManualTime Time) Create()
    {
        var time = new ManualTime(Start);
        var store = new InboxStore(new InboxConfig(), root, new InboxStream(), time);
        return (store, time);
    }

    private static InboxOpenRequest Request(
        string kind,
        string resourceKey,
        string? severity = null)
        => new()
        {
            Kind = kind,
            Severity = severity,
            Subject = new InboxSubject { ResourceKey = resourceKey },
            OpenedSeq = 1
        };

    private static string Describe(InboxItem item)
        => string.Join('|',
            item.Id, item.Kind, item.Severity, item.Title, item.State, item.ResolutionCode,
            item.CreatedAtUtc.ToString("O"), item.UpdatedAtUtc.ToString("O"),
            item.SnoozeUntilUtc?.ToString("O"), item.ResolvedAtUtc?.ToString("O"),
            item.ExpiresAtUtc?.ToString("O"), item.Subject.ResourceKey);

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

    /// <summary>A clock the test moves by hand, so TTL and snooze assertions do not sleep.</summary>
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }
}
