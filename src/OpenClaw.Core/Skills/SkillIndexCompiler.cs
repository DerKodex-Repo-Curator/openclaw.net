using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Skills;

/// <summary>
/// Why a compile was requested. Only used for logging and the reindex response.
/// </summary>
public enum SkillIndexCompileTrigger
{
    /// <summary>First compile performed while the host starts.</summary>
    Startup = 0,

    /// <summary>A skill file changed on disk.</summary>
    FileChange = 1,

    /// <summary>An operator called <c>POST /api/admin/skills/reindex</c>.</summary>
    Manual = 2
}

/// <summary>
/// The single writer that turns the skill directories into a versioned <see cref="SkillIndex"/>.
/// </summary>
/// <remarks>
/// <para>
/// There must be exactly one authoritative parse of the skill tree. Previously both
/// <c>AgentRuntime.ReloadSkillsAsync</c> and <c>MafAgentRuntime.ReloadSkillsAsync</c> called
/// <see cref="SkillLoader.LoadAll"/> directly, so a second (differently timed) parse could observe a
/// different tree than the one the index reports. The compiler replaces both call sites: runtimes
/// consume <see cref="Current"/> instead of parsing.
/// </para>
/// <para>
/// Last known good: a rejected attempt (any fatal diagnostic) never replaces the active index, so
/// breaking one <c>SKILL.md</c> cannot take every other skill offline. The rejected attempt is kept
/// in <see cref="SkillIndexSnapshot.LastAttempt"/> so callers can still see the diagnostics.
/// </para>
/// <para>
/// Concurrency: compiles are serialized by a semaphore (single writer) and stampeding requests are
/// merged — a request that arrives while a compile is running only asks for one extra pass instead
/// of queueing its own.
/// </para>
/// </remarks>
public sealed class SkillIndexCompiler : IHostedService
{
    private readonly SkillsConfig _config;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    private Task<SkillIndexSnapshot>? _inFlight;
    private bool _rerunRequested;
    private long _sequence;
    private SkillIndexSnapshot _current = SkillIndexSnapshot.Empty;
    private string? _workspacePath;
    private IReadOnlyList<string>? _pluginSkillDirs;

    public SkillIndexCompiler(SkillsConfig config, ILogger<SkillIndexCompiler> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>The index currently served to the runtime, probes and endpoints.</summary>
    public SkillIndexSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Supplies the scan inputs that are only known after plugins are loaded. Must be called before
    /// the first compile; calling it later only affects subsequent compiles.
    /// </summary>
    public void Configure(string? workspacePath, IReadOnlyList<string>? pluginSkillDirs)
    {
        _workspacePath = workspacePath;
        _pluginSkillDirs = pluginSkillDirs;
    }

    /// <summary>
    /// Compiles once during host startup so readiness never observes an un-compiled index. A no-op
    /// when the composition root already compiled synchronously.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Current.Version > 0)
            return;

        await CompileAsync(SkillIndexCompileTrigger.Startup, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Requests a compile and returns the resulting snapshot. Concurrent callers share the same
    /// compile; a caller that arrives mid-compile only asks for one additional pass.
    /// </summary>
    public Task<SkillIndexSnapshot> CompileAsync(
        SkillIndexCompileTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_inFlight is null)
            {
                _inFlight = RunAsync(trigger, cancellationToken);
                return _inFlight;
            }

            _rerunRequested = true;
            return _inFlight;
        }
    }

    private async Task<SkillIndexSnapshot> RunAsync(SkillIndexCompileTrigger trigger, CancellationToken cancellationToken)
    {
        // Guarantees the task returned by CompileAsync never completes synchronously, which would
        // otherwise let the caller publish a finished task as _inFlight and never clear it.
        await Task.Yield();

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            while (true)
            {
                lock (_sync)
                {
                    _rerunRequested = false;
                }

                var snapshot = await CompileOnceAsync(trigger, cancellationToken).ConfigureAwait(false);

                lock (_sync)
                {
                    if (!_rerunRequested)
                        return snapshot;
                }
            }
        }
        finally
        {
            // Clear before releasing the semaphore so a caller that acquires it next always starts a
            // fresh compile instead of joining this (already finished) one.
            lock (_sync)
            {
                _inFlight = null;
            }

            _gate.Release();
        }
    }

    private async Task<SkillIndexSnapshot> CompileOnceAsync(
        SkillIndexCompileTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
        {
            // Disabled skills are not "unready": the gateway has nothing to wait for. Publishing an
            // empty, diagnostic-free index keeps readiness and the meta gate green.
            var disabledVersion = Interlocked.Increment(ref _sequence);
            var disabled = new SkillIndex(disabledVersion, string.Empty, DateTimeOffset.UtcNow, [], []);
            return Publish(new SkillIndexSnapshot(disabled, disabled), disabled, trigger);
        }

        SkillLoadResult load;
        try
        {
            var workspacePath = _workspacePath;
            var pluginSkillDirs = _pluginSkillDirs;
            load = await Task
                .Run(() => SkillLoader.LoadAllDetailed(_config, workspacePath, _logger, pluginSkillDirs), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skill index compile failed while scanning skill directories.");
            load = new SkillLoadResult(
                [],
                [SkillDiagnostic.Error(_workspacePath ?? string.Empty, "skill_scan_failed", ex.Message)],
                []);
        }

        var fingerprint = SkillFingerprint.Compute(load.Files);
        var version = Interlocked.Increment(ref _sequence);
        var candidate = new SkillIndex(version, fingerprint, DateTimeOffset.UtcNow, load.Skills, load.Diagnostics);
        var previous = Current;

        var next = candidate.Publishable
            ? new SkillIndexSnapshot(candidate, candidate)
            : new SkillIndexSnapshot(previous.Active, candidate);

        return Publish(next, candidate, trigger);
    }

    private SkillIndexSnapshot Publish(
        SkillIndexSnapshot snapshot,
        SkillIndex candidate,
        SkillIndexCompileTrigger trigger)
    {
        Interlocked.Exchange(ref _current, snapshot);

        if (candidate.Publishable)
        {
            _logger.LogInformation(
                "Published skill index v{Version} ({SkillCount} skills, {FileCount} files, state={State}) after {Trigger} compile.",
                candidate.Version,
                candidate.Skills.Count,
                candidate.Diagnostics.Count,
                candidate.StateName,
                trigger);
        }
        else
        {
            _logger.LogWarning(
                "Rejected skill index attempt v{Version} ({DiagnosticCount} diagnostics); keeping v{ActiveVersion} active.",
                candidate.Version,
                candidate.Diagnostics.Count,
                snapshot.Version);

            foreach (var diagnostic in candidate.Diagnostics.Where(static d => d.Fatal).Take(10))
            {
                _logger.LogWarning(
                    "Skill diagnostic {Code} at {Path}: {Message}",
                    diagnostic.Code,
                    diagnostic.Path,
                    diagnostic.Message);
            }
        }

        return snapshot;
    }
}
