using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Inbox;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Skills;

namespace OpenClaw.Gateway.Inbox;

/// <summary>
/// Turns observable state into durable todo items (design §10.2): run events as they are appended,
/// MCP health transitions as they happen, and the skill index on every sweep.
/// </summary>
/// <remarks>
/// <para>
/// The inbox is a <em>projection</em>, which decides three things. It never blocks a producer (the
/// queue drops and counts instead). It never fails a state transition (every apply is wrapped, and a
/// broken journal line must not stop the next one). And it is rebuildable: every item it creates
/// carries an id derived from the event that opened it, so replaying the journals reproduces the same
/// set (see <see cref="RebuildAsync"/>).
/// </para>
/// <para>
/// Two sources are edge-driven (run events, MCP transitions) and one is level-driven (skill state).
/// The level-driven pass also re-applies the edge-driven snapshots on every sweep, which is what
/// makes a dropped queue item a delay rather than a permanent hole.
/// </para>
/// </remarks>
public sealed class InboxProjector : BackgroundService
{
    private static readonly TimeSpan MinimumSweepInterval = TimeSpan.FromSeconds(1);

    /// <summary>Resource key for the one process-wide skill index.</summary>
    private const string SkillResourceKey = "skills";

    private readonly InboxStore store;
    private readonly InboxProjectionQueue queue;
    private readonly InboxConfig config;
    private readonly McpServerHealthStore? mcpHealth;
    private readonly SkillIndexCompiler? skills;
    private readonly MetaRunEventLog runEvents;
    private readonly TimeProvider time;
    private readonly ILogger<InboxProjector> logger;
    private readonly SemaphoreSlim rebuildGate = new(1, 1);

    public InboxProjector(
        InboxStore store,
        InboxProjectionQueue queue,
        InboxConfig config,
        McpServerHealthStore? mcpHealth,
        SkillIndexCompiler? skills,
        MetaRunEventLog runEvents,
        ILogger<InboxProjector> logger,
        TimeProvider? time = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.mcpHealth = mcpHealth;
        this.skills = skills;
        this.runEvents = runEvents ?? throw new ArgumentNullException(nameof(runEvents));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.time = time ?? TimeProvider.System;
    }

    public long DroppedWork => queue.DroppedCount;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (mcpHealth is not null)
            mcpHealth.Changed += OnMcpChanged;

        await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (mcpHealth is not null)
            mcpHealth.Changed -= OnMcpChanged;

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.WhenAll(ConsumeAsync(stoppingToken), SweepAsync(stoppingToken));

    /// <summary>
    /// Recomputes every item from the run journals plus the current MCP and skill state, then hands
    /// the result to the store, which preserves what humans already decided.
    /// </summary>
    public async Task<InboxRebuildResponse> RebuildAsync(CancellationToken cancellationToken = default)
    {
        await rebuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var shadow = new Dictionary<string, InboxItem>(StringComparer.Ordinal);
            var eventsRead = 0;

            // The journals are the record of what happened, so the set of journals on disk is
            // exactly the set of runs to replay - no ledger page walk, and a run whose record was
            // already dropped by retention still contributes its history.
            foreach (var invocationId in runEvents.EnumerateInvocations())
            {
                var read = runEvents.Read(invocationId);
                eventsRead += read.Events.Count;
                foreach (var runEvent in read.Events)
                    ReplayRunEvent(shadow, runEvent);
            }

            ReplayMcp(shadow);
            ReplaySkills(shadow, time.GetUtcNow());

            var count = await store.RebuildAsync([.. shadow.Values], cancellationToken).ConfigureAwait(false);
            return new InboxRebuildResponse(count, eventsRead, store.SkippedLines);
        }
        finally
        {
            rebuildGate.Release();
        }
    }

    private void OnMcpChanged(McpHealthTransition transition) => queue.Publish(transition);

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var work in queue.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    switch (work)
                    {
                        case InboxProjectionWork.Run run:
                            await ProjectAsync(run.Event, cancellationToken).ConfigureAwait(false);
                            break;
                        case InboxProjectionWork.Mcp mcp:
                            await ProjectMcpAsync(mcp.Transition, cancellationToken).ConfigureAwait(false);
                            break;
                    }
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or JsonException
                    or InvalidOperationException)
                {
                    // One bad item must not stop the projection: the sweep and a rebuild both revisit.
                    logger.LogWarning(exception, "Inbox projection failed for {WorkType}.", work.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var interval = config.SweepInterval > MinimumSweepInterval ? config.SweepInterval : MinimumSweepInterval;
        using var timer = new PeriodicTimer(interval, time);

        try
        {
            // One pass before the first tick: a process that starts with a server already down, or
            // with a degraded index, shows it in the list without waiting a whole interval.
            await SweepGuardedAsync(cancellationToken).ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await SweepGuardedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// One sweep, with a disk failure contained: the inbox is a projection, and a full disk must
    /// degrade the todo list rather than take the gateway's hosted services down with it.
    /// </summary>
    private async Task SweepGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SweepOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            logger.LogWarning(exception, "Inbox sweep failed; the next tick retries.");
        }
    }

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        await store.SweepAsync(cancellationToken).ConfigureAwait(false);
        await ProjectMcpSnapshotAsync(cancellationToken).ConfigureAwait(false);
        await ProjectSkillStateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the current MCP fleet, not a transition. Covers a restart (no history to replay), a
    /// dropped queue item, and a recovery that happened while the projector was busy.
    /// </summary>
    private async Task ProjectMcpSnapshotAsync(CancellationToken cancellationToken)
    {
        if (mcpHealth is null)
            return;

        foreach (var server in mcpHealth.Snapshot())
        {
            if (server.State == McpServerHealthState.Down)
                await OpenMcpItemAsync(server, cancellationToken).ConfigureAwait(false);
            else
                await store.ResolveByResourceAsync(
                    InboxKinds.McpDown, server.ServerId, InboxResolutionCodes.Recovered, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>Projects one MCP health transition; internal for the same reason as <see cref="ProjectAsync"/>.</summary>
    internal async Task ProjectMcpAsync(McpHealthTransition transition, CancellationToken cancellationToken)
    {
        var server = transition.Health;

        if (server.State == McpServerHealthState.Down)
        {
            await OpenMcpItemAsync(server, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (transition.PreviousState == McpServerHealthState.Down)
        {
            await store.ResolveByResourceAsync(
                InboxKinds.McpDown, server.ServerId, InboxResolutionCodes.Recovered, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private Task OpenMcpItemAsync(McpServerHealth server, CancellationToken cancellationToken)
        => store.OpenAsync(
            new InboxOpenRequest
            {
                Kind = InboxKinds.McpDown,
                Severity = InboxSeverities.Critical,
                Subject = new InboxSubject { ServerId = server.ServerId, ResourceKey = server.ServerId },
                OpenedSeq = 0,
                CreatedAtUtc = server.LastProbeAtUtc ?? time.GetUtcNow()
            },
            cancellationToken);

    /// <summary>
    /// The skill index has no change event, so its state is read on every sweep. Only
    /// <c>degraded</c> opens an item: <c>unready</c> is the normal state for the first second after
    /// start-up, and a todo that appears on every boot would be noise, not a task.
    /// </summary>
    private async Task ProjectSkillStateAsync(CancellationToken cancellationToken)
    {
        if (skills is null)
            return;

        var snapshot = skills.Current;
        if (snapshot.State == SkillIndexState.Degraded)
        {
            await store.OpenAsync(
                new InboxOpenRequest
                {
                    Kind = InboxKinds.SkillDegraded,
                    Subject = new InboxSubject { ResourceKey = SkillResourceKey },
                    OpenedSeq = 0,
                    CreatedAtUtc = snapshot.CompiledAtUtc == DateTimeOffset.MinValue
                        ? time.GetUtcNow()
                        : snapshot.CompiledAtUtc
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (snapshot.State == SkillIndexState.Ready)
        {
            await store.ResolveByResourceAsync(
                InboxKinds.SkillDegraded, SkillResourceKey, InboxResolutionCodes.Resolved, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Projects one run event. Internal so a test can drive the mapping without standing up the
    /// ledger's write path - the same body the queue consumer runs.
    /// </summary>
    internal async Task ProjectAsync(MetaRunEvent runEvent, CancellationToken cancellationToken)
    {
        var key = InvocationKey(runEvent.InvocationId);
        var subject = new InboxSubject
        {
            InvocationId = runEvent.InvocationId,
            StepId = runEvent.StepId,
            ResourceKey = key
        };

        switch (runEvent.Kind)
        {
            case MetaRunEventKinds.ApprovalRequested:
                await store.OpenAsync(
                    new InboxOpenRequest
                    {
                        Kind = InboxKinds.Approval,
                        Severity = InboxSeverities.Warning,
                        Subject = subject with { ApprovalId = runEvent.Detail },
                        OpenedSeq = runEvent.Seq,
                        CreatedAtUtc = runEvent.AtUtc
                    },
                    cancellationToken).ConfigureAwait(false);

                // The approval is the more specific ask; the generic "run is paused" row would be a
                // second todo for the same blocking fact.
                await store.ResolveByResourceAsync(
                    InboxKinds.RunPaused, key, InboxResolutionCodes.AutoClosed, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case MetaRunEventKinds.ApprovalDecided:
                await store.ResolveByResourceAsync(
                    InboxKinds.Approval,
                    key,
                    string.Equals(runEvent.Detail, "denied", StringComparison.Ordinal)
                        ? InboxResolutionCodes.Rejected
                        : InboxResolutionCodes.Approved,
                    cancellationToken).ConfigureAwait(false);
                break;

            case MetaRunEventKinds.StepPaused:
                await store.OpenAsync(
                    new InboxOpenRequest
                    {
                        Kind = InboxKinds.RunPaused,
                        Severity = InboxSeverities.Info,
                        Subject = subject,
                        OpenedSeq = runEvent.Seq,
                        CreatedAtUtc = runEvent.AtUtc
                    },
                    cancellationToken).ConfigureAwait(false);
                break;

            case MetaRunEventKinds.Resumed:
                await store.ResolveByResourceAsync(
                    InboxKinds.RunPaused, key, InboxResolutionCodes.AutoClosed, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case MetaRunEventKinds.Completed:
                await store.ResolveByResourceAsync(
                    InboxKinds.RunPaused, key, InboxResolutionCodes.Completed, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case MetaRunEventKinds.Failed:
            case MetaRunEventKinds.Uncertain:
                await store.OpenAsync(
                    new InboxOpenRequest
                    {
                        Kind = InboxKinds.RunFailed,
                        // "uncertain" is a run we cannot account for, not a run we watched fail.
                        Severity = runEvent.Kind == MetaRunEventKinds.Failed
                            ? InboxSeverities.Critical
                            : InboxSeverities.Warning,
                        Subject = subject,
                        OpenedSeq = runEvent.Seq,
                        CreatedAtUtc = runEvent.AtUtc
                    },
                    cancellationToken).ConfigureAwait(false);

                await store.ResolveByResourceAsync(
                    InboxKinds.RunPaused, key, InboxResolutionCodes.Failed, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }
    }

    private void ReplayRunEvent(Dictionary<string, InboxItem> shadow, MetaRunEvent runEvent)
    {
        var key = InvocationKey(runEvent.InvocationId);
        var atUtc = runEvent.AtUtc;
        var subject = new InboxSubject
        {
            InvocationId = runEvent.InvocationId,
            StepId = runEvent.StepId,
            ResourceKey = key
        };

        switch (runEvent.Kind)
        {
            case MetaRunEventKinds.ApprovalRequested:
                InboxReplay.Open(
                    shadow, config, InboxKinds.Approval, key, runEvent.Seq,
                    subject with { ApprovalId = runEvent.Detail }, atUtc, InboxSeverities.Warning);
                InboxReplay.Resolve(shadow, InboxKinds.RunPaused, key, InboxResolutionCodes.AutoClosed, atUtc);
                break;

            case MetaRunEventKinds.ApprovalDecided:
                InboxReplay.Resolve(
                    shadow, InboxKinds.Approval, key,
                    string.Equals(runEvent.Detail, "denied", StringComparison.Ordinal)
                        ? InboxResolutionCodes.Rejected
                        : InboxResolutionCodes.Approved,
                    atUtc);
                break;

            case MetaRunEventKinds.StepPaused:
                InboxReplay.Open(
                    shadow, config, InboxKinds.RunPaused, key, runEvent.Seq, subject, atUtc, InboxSeverities.Info);
                break;

            case MetaRunEventKinds.Resumed:
                InboxReplay.Resolve(shadow, InboxKinds.RunPaused, key, InboxResolutionCodes.AutoClosed, atUtc);
                break;

            case MetaRunEventKinds.Completed:
                InboxReplay.Resolve(shadow, InboxKinds.RunPaused, key, InboxResolutionCodes.Completed, atUtc);
                break;

            case MetaRunEventKinds.Failed:
            case MetaRunEventKinds.Uncertain:
                InboxReplay.Open(
                    shadow, config, InboxKinds.RunFailed, key, runEvent.Seq, subject, atUtc,
                    runEvent.Kind == MetaRunEventKinds.Failed ? InboxSeverities.Critical : InboxSeverities.Warning);
                InboxReplay.Resolve(shadow, InboxKinds.RunPaused, key, InboxResolutionCodes.Failed, atUtc);
                break;
        }
    }

    private void ReplayMcp(Dictionary<string, InboxItem> shadow)
    {
        if (mcpHealth is null)
            return;

        foreach (var server in mcpHealth.Snapshot())
        {
            if (server.State == McpServerHealthState.Down)
            {
                InboxReplay.Open(
                    shadow, config, InboxKinds.McpDown, server.ServerId, 0,
                    new InboxSubject { ServerId = server.ServerId, ResourceKey = server.ServerId },
                    server.LastProbeAtUtc ?? time.GetUtcNow(),
                    InboxSeverities.Critical);
            }
        }
    }

    private void ReplaySkills(Dictionary<string, InboxItem> shadow, DateTimeOffset now)
    {
        if (skills?.Current.State != SkillIndexState.Degraded)
            return;

        var compiledAt = skills.Current.CompiledAtUtc;
        InboxReplay.Open(
            shadow, config, InboxKinds.SkillDegraded, SkillResourceKey, 0,
            new InboxSubject { ResourceKey = SkillResourceKey },
            compiledAt == DateTimeOffset.MinValue ? now : compiledAt);
    }

    private static string InvocationKey(Guid invocationId)
        => invocationId.ToString("N", System.Globalization.CultureInfo.InvariantCulture);
}
