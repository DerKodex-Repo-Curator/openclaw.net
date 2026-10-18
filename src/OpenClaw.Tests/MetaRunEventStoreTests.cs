using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B3 journal, store level: what gets written, in what order, and what survives a broken disk.
/// The HTTP surface is covered separately in <see cref="MetaRunEventEndpointTests"/>.
/// </summary>
public sealed class MetaRunEventStoreTests
{
    private static readonly MetaInvocationPauseInfo ToolApprovalPause =
        new("tool_approval", "refund", "Approve the refund of 120.00 to order 4711?", "map_7f3a91c2d4e5aabb");

    [Fact]
    public async Task T_B3_1_A_normal_run_records_claim_steps_and_completion_in_order()
    {
        var storagePath = CreateStoragePath();
        try
        {
            using var store = new MetaInvocationStore(storagePath, retentionDays: 30);
            var claim = await store.BeginOrGetAsync(
                "caller", "key-1", "hash", TestContext.Current.CancellationToken, sessionId: "s-1", skill: "triage-order");

            await store.CompleteAsync(
                "caller",
                "key-1",
                "done",
                TestContext.Current.CancellationToken,
                [
                    new MetaRunEventDraft(MetaRunEventKinds.StepCompleted, "validate", "completed", null, Synthetic: true),
                    new MetaRunEventDraft(MetaRunEventKinds.StepCompleted, "refund", "completed", null, Synthetic: true)
                ]);

            var events = store.Events.Read(claim.Record.InvocationId).Events;

            Assert.Equal(
                ["claim", "step.completed", "step.completed", "completed"],
                events.Select(runEvent => runEvent.Kind));
            Assert.Equal([1L, 2L, 3L, 4L], events.Select(runEvent => runEvent.Seq));
            Assert.All(events, runEvent => Assert.Equal(claim.Record.InvocationId, runEvent.InvocationId));
            Assert.Equal("Completed", events[^1].Status);
            Assert.Equal("s-1", claim.Record.SessionId);
            Assert.Equal("triage-order", claim.Record.Skill);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task T_B3_2_A_pause_records_the_reason_and_the_approval_it_waits_on()
    {
        var storagePath = CreateStoragePath();
        try
        {
            using var store = new MetaInvocationStore(storagePath, retentionDays: 30);
            var claim = await store.BeginOrGetAsync("caller", "key-2", "hash", TestContext.Current.CancellationToken);

            var paused = await store.MarkAwaitingInputAsync(
                "caller",
                "key-2",
                ToolApprovalPause,
                TestContext.Current.CancellationToken,
                [
                    new MetaRunEventDraft(MetaRunEventKinds.StepCompleted, "validate", "completed", null, Synthetic: true),
                    new MetaRunEventDraft(MetaRunEventKinds.StepStarted, "refund", "Running", null, Synthetic: true)
                ]);

            Assert.Equal(MetaInvocationStatus.AwaitingInput, paused.Status);

            // The two events the service appends around a resume: the decision, then the resume.
            await store.AppendEventAsync(
                claim.Record.InvocationId,
                [new MetaRunEventDraft(MetaRunEventKinds.ApprovalDecided, "refund", "AwaitingInput", "approved")],
                TestContext.Current.CancellationToken);
            await store.AppendEventAsync(
                claim.Record.InvocationId,
                [new MetaRunEventDraft(MetaRunEventKinds.Resumed, "refund", "Running")],
                TestContext.Current.CancellationToken);

            await store.CompleteAsync(
                "caller",
                "key-2",
                "done",
                TestContext.Current.CancellationToken,
                [new MetaRunEventDraft(MetaRunEventKinds.StepCompleted, "refund", "completed", null, Synthetic: true)]);

            var events = store.Events.Read(claim.Record.InvocationId).Events;
            Assert.Equal(
                [
                    "claim",
                    "step.completed",
                    "step.started",
                    "step.paused",
                    "approval.requested",
                    "approval.decided",
                    "resumed",
                    "step.completed",
                    "completed"
                ],
                events.Select(runEvent => runEvent.Kind));

            var pauseEvent = events.Single(runEvent => runEvent.Kind == MetaRunEventKinds.StepPaused);
            Assert.Equal("tool_approval", pauseEvent.Detail);
            Assert.Equal("refund", pauseEvent.StepId);
            Assert.Equal("AwaitingInput", pauseEvent.Status);

            // The approval id is shortened: a timeline must never carry a usable handle.
            var requested = events.Single(runEvent => runEvent.Kind == MetaRunEventKinds.ApprovalRequested);
            Assert.Equal("map_7f3a91c2d4e5", requested.Detail);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task T_B3_2_Repeating_the_same_pause_does_not_duplicate_the_event()
    {
        var storagePath = CreateStoragePath();
        try
        {
            using var store = new MetaInvocationStore(storagePath, retentionDays: 30);
            var claim = await store.BeginOrGetAsync("caller", "key-2b", "hash", TestContext.Current.CancellationToken);

            await store.MarkAwaitingInputAsync("caller", "key-2b", ToolApprovalPause, TestContext.Current.CancellationToken);
            await store.MarkAwaitingInputAsync("caller", "key-2b", ToolApprovalPause, TestContext.Current.CancellationToken);

            var events = store.Events.Read(claim.Record.InvocationId).Events;
            Assert.Single(events, runEvent => runEvent.Kind == MetaRunEventKinds.StepPaused);
            Assert.Single(events, runEvent => runEvent.Kind == MetaRunEventKinds.ApprovalRequested);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task T_B3_2_Synthetic_step_events_are_not_re_emitted_on_a_later_observation()
    {
        var storagePath = CreateStoragePath();
        try
        {
            using var store = new MetaInvocationStore(storagePath, retentionDays: 30);
            var claim = await store.BeginOrGetAsync("caller", "key-2c", "hash", TestContext.Current.CancellationToken);

            // The same derived step is reported twice (pause, then terminal). Dedup keeps one.
            await store.MarkAwaitingInputAsync(
                "caller",
                "key-2c",
                new MetaInvocationPauseInfo("user_input", "ask", "Which region?", null),
                TestContext.Current.CancellationToken,
                [new MetaRunEventDraft(MetaRunEventKinds.StepCompleted, "validate", "completed", null, Synthetic: true)]);
            await store.CompleteAsync(
                "caller",
                "key-2c",
                "done",
                TestContext.Current.CancellationToken,
                [
                    new MetaRunEventDraft(MetaRunEventKinds.StepCompleted, "validate", "completed", null, Synthetic: true),
                    new MetaRunEventDraft(MetaRunEventKinds.StepCompleted, "ask", "completed", null, Synthetic: true)
                ]);

            var events = store.Events.Read(claim.Record.InvocationId).Events;
            Assert.Single(events, runEvent =>
                runEvent.Kind == MetaRunEventKinds.StepCompleted && runEvent.StepId == "validate");
            Assert.Single(events, runEvent =>
                runEvent.Kind == MetaRunEventKinds.StepCompleted && runEvent.StepId == "ask");
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task T_B3_3_Reading_after_a_seq_is_strictly_greater_and_repeatable()
    {
        var storagePath = CreateStoragePath();
        try
        {
            using var store = new MetaInvocationStore(storagePath, retentionDays: 30);
            var claim = await store.BeginOrGetAsync("caller", "key-3", "hash", TestContext.Current.CancellationToken);
            await store.MarkAwaitingInputAsync(
                "caller",
                "key-3",
                ToolApprovalPause,
                TestContext.Current.CancellationToken,
                [new MetaRunEventDraft(MetaRunEventKinds.StepCompleted, "validate", "completed", null, Synthetic: true)]);

            var all = store.Events.Read(claim.Record.InvocationId).Events;
            var afterTwo = all.Where(runEvent => runEvent.Seq > 2).ToList();
            var again = all.Where(runEvent => runEvent.Seq > 2).ToList();

            Assert.Equal([3L, 4L], afterTwo.Select(runEvent => runEvent.Seq));
            Assert.Equal(afterTwo, again);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task T_B3_6_A_truncated_journal_line_is_skipped_and_reported_as_a_gap()
    {
        var storagePath = CreateStoragePath();
        try
        {
            using var store = new MetaInvocationStore(storagePath, retentionDays: 30);
            var claim = await store.BeginOrGetAsync("caller", "key-6", "hash", TestContext.Current.CancellationToken);
            await store.CompleteAsync("caller", "key-6", "done", TestContext.Current.CancellationToken);

            // Simulate a crash mid-write: a line that is a prefix of a real record.
            var path = store.Events.PathFor(claim.Record.InvocationId);
            await File.AppendAllTextAsync(path, """{"invocationId":"3d9f8a71-2b4c-4d5e-9f60-1a2b3c4d""");

            var read = store.Events.Read(claim.Record.InvocationId);

            Assert.Equal(["claim", "completed"], read.Events.Select(runEvent => runEvent.Kind));
            Assert.Equal(1, read.UnreadableLines);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task T_B3_8_Concurrent_claims_of_one_key_produce_a_single_run()
    {
        var storagePath = CreateStoragePath();
        try
        {
            using var first = new MetaInvocationStore(storagePath, retentionDays: 30);
            using var second = new MetaInvocationStore(storagePath, retentionDays: 30);

            var claims = await Task.WhenAll(
                first.BeginOrGetAsync("caller", "key-8", "hash", TestContext.Current.CancellationToken),
                second.BeginOrGetAsync("caller", "key-8", "hash", TestContext.Current.CancellationToken));

            Assert.Single(claims, claim => claim.Kind == MetaInvocationClaimKind.Started);
            Assert.Equal(claims[0].Record.InvocationId, claims[1].Record.InvocationId);

            // And the journal has exactly one claim event: a double claim would leave two.
            var events = first.Events.Read(claims[0].Record.InvocationId).Events;
            Assert.Single(events, runEvent => runEvent.Kind == MetaRunEventKinds.Claim);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task T_B3_9_Retention_deletes_the_journal_with_the_record()
    {
        var storagePath = CreateStoragePath();
        try
        {
            using var store = new MetaInvocationStore(storagePath, retentionDays: 1);
            var claim = await store.BeginOrGetAsync("caller", "key-9", "hash", TestContext.Current.CancellationToken);
            await store.CompleteAsync("caller", "key-9", "done", TestContext.Current.CancellationToken);

            var journal = store.Events.PathFor(claim.Record.InvocationId);
            Assert.True(File.Exists(journal));

            // Age the ledger row past retention, then trigger the sweep with a fresh claim. Rewriting
            // the ledger through its own contract is deliberate: string-patching a timestamp would
            // depend on how System.Text.Json happens to format fractional seconds.
            var ledgerPath = Path.Combine(storagePath, "meta-invocations.json");
            var ledger = System.Text.Json.JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(ledgerPath),
                MetaInvocationJsonContext.Default.ListMetaInvocationRecord)!;
            var aged = ledger
                .Select(record => record with { CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-30) })
                .ToList();
            await File.WriteAllTextAsync(
                ledgerPath,
                System.Text.Json.JsonSerializer.Serialize(aged, MetaInvocationJsonContext.Default.ListMetaInvocationRecord));

            await store.BeginOrGetAsync("caller", "key-9b", "hash", TestContext.Current.CancellationToken);

            Assert.False(File.Exists(journal));
            Assert.Null(await store.TryGetByInvocationIdAsync(claim.Record.InvocationId, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task T_B3_10_A_failed_journal_write_advances_state_and_marks_the_gap()
    {
        var storagePath = CreateStoragePath();
        try
        {
            using var store = new MetaInvocationStore(storagePath, retentionDays: 30);

            // Make the journal directory unusable without touching the ledger: a *file* where the
            // directory must be is the cheapest portable way to fail the append.
            await File.WriteAllTextAsync(Path.Combine(storagePath, "meta-invocations"), "not a directory");

            var claim = await store.BeginOrGetAsync("caller", "key-10", "hash", TestContext.Current.CancellationToken);
            await store.CompleteAsync("caller", "key-10", "done", TestContext.Current.CancellationToken);

            Assert.Equal(MetaInvocationStatus.Running, claim.Record.Status);
            Assert.NotNull(await store.TryGetByInvocationIdAsync(claim.Record.InvocationId, TestContext.Current.CancellationToken));

            var record = await store.TryGetByInvocationIdAsync(claim.Record.InvocationId, TestContext.Current.CancellationToken);
            Assert.True(record!.EventsGap);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task The_run_index_pages_by_cursor_without_repeating_or_skipping()
    {
        var storagePath = CreateStoragePath();
        try
        {
            using var store = new MetaInvocationStore(storagePath, retentionDays: 30);
            for (var index = 0; index < 5; index++)
            {
                await store.BeginOrGetAsync(
                    "caller",
                    $"key-page-{index}",
                    "hash",
                    TestContext.Current.CancellationToken,
                    sessionId: "s-page",
                    skill: "triage-order");
            }

            var seen = new List<Guid>();
            DateTimeOffset? cursorCreated = null;
            Guid? cursorId = null;
            for (var page = 0; page < 5; page++)
            {
                var (items, hasMore) = await store.ListAsync(
                    callerId: null,
                    sessionId: "s-page",
                    status: MetaInvocationStatus.Running,
                    limit: 2,
                    cursorCreated,
                    cursorId,
                    TestContext.Current.CancellationToken);

                seen.AddRange(items.Select(item => item.InvocationId));
                if (!hasMore || items.Count == 0)
                    break;

                cursorCreated = items[^1].CreatedAtUtc;
                cursorId = items[^1].InvocationId;
            }

            Assert.Equal(5, seen.Count);
            Assert.Equal(5, seen.Distinct().Count());
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    private static string CreateStoragePath()
    {
        var path = Path.Join(Path.GetTempPath(), "openclaw-meta-run-events-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
