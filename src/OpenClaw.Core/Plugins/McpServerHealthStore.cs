namespace OpenClaw.Core.Plugins;

/// <summary>
/// Shared, thread-safe view of every MCP server's health.
/// </summary>
/// <remarks>
/// <para>
/// The store owns the state machine and nothing else: it never talks to an MCP server. The probe
/// loop feeds it results, and the executor, the endpoints and the metrics read it. Keeping the
/// thresholds here is what makes the transition rules testable without a live server.
/// </para>
/// <para>
/// The state machine exists to avoid flapping (design §8.1): one timeout must not hide a tool, so
/// <see cref="McpHealthConfig.FailureThreshold"/> only degrades, <see cref="McpHealthConfig.DownThreshold"/>
/// hides, and recovery needs <see cref="McpHealthConfig.RecoveryThreshold"/> consecutive successes.
/// </para>
/// </remarks>
public sealed class McpServerHealthStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, McpServerHealth> _servers = new(StringComparer.Ordinal);
    private long _revision;

    public McpServerHealthStore(McpHealthConfig config)
    {
        Config = config.Normalize();
    }

    /// <summary>Clamped settings this store applies.</summary>
    public McpHealthConfig Config { get; }

    /// <summary>
    /// Increments every time a server's <em>state</em> changes (not on latency/counter updates).
    /// Streams use it to push state changes without turning into a heartbeat.
    /// </summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>Raised after a state change, outside the lock. Subscribers must not throw.</summary>
    public event Action<McpHealthTransition>? Changed;

    /// <summary>Every known server, ordered by id so responses are stable.</summary>
    public IReadOnlyList<McpServerHealth> Snapshot()
    {
        lock (_gate)
        {
            return [.. _servers.Values.OrderBy(static s => s.ServerId, StringComparer.Ordinal)];
        }
    }

    public McpServerHealth Get(string serverId)
    {
        lock (_gate)
        {
            return _servers.TryGetValue(serverId, out var health)
                ? health
                : new McpServerHealth { ServerId = serverId };
        }
    }

    /// <summary>Whether the server's tools may be served right now.</summary>
    public bool IsAvailable(string serverId)
    {
        lock (_gate)
        {
            return !_servers.TryGetValue(serverId, out var health) || health.State != McpServerHealthState.Down;
        }
    }

    /// <summary>Whether the server is down (used for the tool-call refusal code).</summary>
    public bool IsDown(string serverId)
    {
        lock (_gate)
        {
            return _servers.TryGetValue(serverId, out var health) && health.State == McpServerHealthState.Down;
        }
    }

    /// <summary>
    /// Registers a server without probing it: a freshly configured or reloaded server starts as
    /// <see cref="McpServerHealthState.Unknown"/> (T-B5-8).
    /// </summary>
    public void Track(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
            return;

        lock (_gate)
        {
            if (!_servers.ContainsKey(serverId))
                _servers[serverId] = new McpServerHealth { ServerId = serverId };
        }
    }

    /// <summary>Forgets a server that is no longer configured.</summary>
    public void Remove(string serverId)
    {
        lock (_gate)
        {
            _servers.Remove(serverId);
        }
    }

    /// <summary>
    /// Records a successful probe. A changed tool inventory is reported through
    /// <see cref="McpHealthTransition.ToolsChanged"/> and (when the server would otherwise be
    /// healthy) demotes it to degraded, because what the model can call no longer matches the plan
    /// the operator approved.
    /// </summary>
    public McpHealthTransition RecordSuccess(
        string serverId,
        long latencyMs,
        IReadOnlyList<string> toolNames,
        DateTimeOffset? atUtc = null)
    {
        var now = atUtc ?? DateTimeOffset.UtcNow;
        var fingerprint = Fingerprint(toolNames);

        McpHealthTransition transition;
        lock (_gate)
        {
            var current = _servers.TryGetValue(serverId, out var existing)
                ? existing
                : new McpServerHealth { ServerId = serverId };

            var toolsChanged = current.ToolFingerprint is not null &&
                               !string.Equals(current.ToolFingerprint, fingerprint, StringComparison.Ordinal);

            var failures = 0;
            var successes = current.ConsecutiveSuccesses + 1;

            var next = current.State switch
            {
                // A recovering server has to prove itself: one good probe after an outage is not
                // enough to bring tools back and then lose them again.
                McpServerHealthState.Down or McpServerHealthState.Degraded =>
                    successes >= Config.RecoveryThreshold
                        ? McpServerHealthState.Healthy
                        : current.State,
                _ => McpServerHealthState.Healthy
            };

            if (next == McpServerHealthState.Healthy && toolsChanged)
                next = McpServerHealthState.Degraded;

            var updated = current with
            {
                State = next,
                LastProbeAtUtc = now,
                LastSuccessAtUtc = now,
                ConsecutiveFailures = failures,
                ConsecutiveSuccesses = successes,
                LatencyMs = latencyMs,
                ToolCount = toolNames.Count,
                LastErrorCode = next == McpServerHealthState.Degraded && toolsChanged
                    ? McpProbeErrorCodes.ToolsChanged
                    : null,
                ToolFingerprint = fingerprint
            };

            _servers[serverId] = updated;
            transition = Build(updated, current.State, toolsChanged);
        }

        Raise(transition);
        return transition;
    }

    /// <summary>Records a failed probe and applies the degrade/down thresholds.</summary>
    public McpHealthTransition RecordFailure(
        string serverId,
        string errorCode,
        DateTimeOffset? atUtc = null)
    {
        var now = atUtc ?? DateTimeOffset.UtcNow;
        var code = McpProbeErrorCodes.IsKnownCode(errorCode) ? errorCode : McpProbeErrorCodes.ServerError;

        McpHealthTransition transition;
        lock (_gate)
        {
            var current = _servers.TryGetValue(serverId, out var existing)
                ? existing
                : new McpServerHealth { ServerId = serverId };

            var failures = current.ConsecutiveFailures + 1;
            var next = failures >= Config.DownThreshold
                ? McpServerHealthState.Down
                : failures >= Config.FailureThreshold
                    ? McpServerHealthState.Degraded
                    : current.State;

            var updated = current with
            {
                State = next,
                LastProbeAtUtc = now,
                ConsecutiveFailures = failures,
                ConsecutiveSuccesses = 0,
                // The latency of the last success is stale the moment a probe fails.
                LatencyMs = null,
                LastErrorCode = code
            };

            _servers[serverId] = updated;
            transition = Build(updated, current.State, ToolsChanged: false);
        }

        Raise(transition);
        return transition;
    }

    private McpHealthTransition Build(McpServerHealth updated, McpServerHealthState previousState, bool ToolsChanged)
    {
        var stateChanged = updated.State != previousState;
        if (stateChanged)
            Interlocked.Increment(ref _revision);

        return new McpHealthTransition(updated, previousState, stateChanged, ToolsChanged);
    }

    private void Raise(McpHealthTransition transition)
    {
        var handler = Changed;
        if (handler is null || !transition.StateChanged)
            return;

        foreach (var subscriber in handler.GetInvocationList())
        {
            try
            {
                ((Action<McpHealthTransition>)subscriber)(transition);
            }
            catch
            {
                // A broken subscriber (a stream writer whose client vanished) must not stop the
                // probe loop or roll back the state it just recorded.
            }
        }
    }

    private static string Fingerprint(IReadOnlyList<string> toolNames)
    {
        var sorted = toolNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal);

        return string.Join('\n', sorted);
    }
}
