using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.Core.Skills;

/// <summary>
/// One problem discovered while compiling a skill index.
/// </summary>
/// <param name="Path">File or directory the problem refers to.</param>
/// <param name="Code">Stable machine-readable code (for example <c>parse_failed</c>).</param>
/// <param name="Message">Human-readable explanation.</param>
/// <param name="Fatal">
/// <see langword="true"/> when the referenced skill could not be produced at all, which makes the
/// whole index unsafe to publish (the previous good index stays active). Non-fatal diagnostics
/// describe problems that were worked around, so the index is still published but reported as
/// degraded.
/// </param>
public sealed record SkillDiagnostic(string Path, string Code, string Message, bool Fatal = false)
{
    /// <summary>A skill (or skill root) is missing from the index because of this problem.</summary>
    public static SkillDiagnostic Error(string path, string code, string message)
        => new(path, code, message, Fatal: true);

    /// <summary>The index is complete, but something was skipped or rewritten.</summary>
    public static SkillDiagnostic Warning(string path, string code, string message)
        => new(path, code, message, Fatal: false);
}

/// <summary>
/// Lifecycle state of a compiled skill index.
/// </summary>
public enum SkillIndexState
{
    /// <summary>No usable index has ever been published (nothing compiled, or every attempt failed).</summary>
    Unready = 0,

    /// <summary>A usable index is active and carries no diagnostics.</summary>
    Ready = 1,

    /// <summary>A usable index is active, but the latest compile attempt reported diagnostics.</summary>
    Degraded = 2
}

/// <summary>
/// An immutable snapshot of every skill the gateway can see, plus the diagnostics produced while
/// producing it. Compiling an index means: parse every <c>SKILL.md</c>, validate meta compositions,
/// and collect resources. No code is generated and nothing is written back to disk.
/// </summary>
public sealed record SkillIndex(
    long Version,
    string Fingerprint,
    DateTimeOffset CompiledAtUtc,
    IReadOnlyList<SkillDefinition> Skills,
    IReadOnlyList<SkillDiagnostic> Diagnostics)
{
    /// <summary>
    /// Whether the runtime may serve traffic from this index.
    /// </summary>
    /// <remarks>
    /// Deliberately decoupled from <see cref="Diagnostics"/>: a degraded index (last-known-good with
    /// a broken sibling skill) is still usable. Use <see cref="State"/> for the three-way answer.
    /// </remarks>
    public bool Ready => Version > 0;

    /// <summary>Any diagnostic at all, fatal or not.</summary>
    public bool HasDiagnostics => Diagnostics.Count > 0;

    /// <summary>
    /// Three-way projection used by <c>/health/ready</c> and <c>GET /api/integration/skill-index</c>.
    /// </summary>
    public SkillIndexState State => Version <= 0
        ? SkillIndexState.Unready
        : HasDiagnostics ? SkillIndexState.Degraded : SkillIndexState.Ready;

    /// <summary>Wire form of <see cref="State"/> (<c>unready</c> / <c>ready</c> / <c>degraded</c>).</summary>
    public string StateName => SkillIndexStates.ToWireValue(State);

    /// <summary>
    /// Whether this index may replace the currently active one. A single fatal diagnostic means the
    /// candidate is incomplete, so the caller must keep the previous index (last known good).
    /// </summary>
    public bool Publishable => !Diagnostics.Any(static d => d.Fatal);
}

/// <summary>Wire helpers for <see cref="SkillIndexState"/>.</summary>
public static class SkillIndexStates
{
    public const string Unready = "unready";
    public const string Ready = "ready";
    public const string Degraded = "degraded";

    public static string ToWireValue(SkillIndexState state) => state switch
    {
        SkillIndexState.Ready => Ready,
        SkillIndexState.Degraded => Degraded,
        _ => Unready
    };

    public static SkillIndexState Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Ready => SkillIndexState.Ready,
        Degraded => SkillIndexState.Degraded,
        _ => SkillIndexState.Unready
    };
}

/// <summary>
/// The compiler's public view: <see cref="Active"/> is what the runtime serves, while
/// <see cref="LastAttempt"/> is whatever the most recent compile produced — it can be newer than
/// <see cref="Active"/> when that attempt was rejected (last known good semantics).
/// </summary>
public sealed record SkillIndexSnapshot(SkillIndex? Active, SkillIndex? LastAttempt)
{
    /// <summary>Nothing has been compiled yet.</summary>
    public static SkillIndexSnapshot Empty { get; } = new(null, null);

    /// <summary>Version of the active index, or <c>0</c> when none was published.</summary>
    public long Version => Active?.Version ?? 0;

    /// <summary>Fingerprint of the active index, or an empty string when none was published.</summary>
    public string Fingerprint => Active?.Fingerprint ?? string.Empty;

    /// <summary>When the active index was compiled.</summary>
    public DateTimeOffset CompiledAtUtc => Active?.CompiledAtUtc ?? DateTimeOffset.MinValue;

    /// <summary>Skills the runtime can currently serve.</summary>
    public IReadOnlyList<SkillDefinition> Skills => Active?.Skills ?? [];

    /// <summary>Diagnostics from the most recent attempt (falls back to the active index).</summary>
    public IReadOnlyList<SkillDiagnostic> Diagnostics => (LastAttempt ?? Active)?.Diagnostics ?? [];

    /// <summary>Three-way state as seen by probes and endpoints.</summary>
    public SkillIndexState State => Version <= 0
        ? SkillIndexState.Unready
        : Diagnostics.Count > 0 ? SkillIndexState.Degraded : SkillIndexState.Ready;

    /// <summary>Wire form of <see cref="State"/>.</summary>
    public string StateName => SkillIndexStates.ToWireValue(State);

    /// <summary>Whether the gateway can serve skill-dependent traffic.</summary>
    public bool Ready => State != SkillIndexState.Unready;
}

/// <summary>
/// One <c>SKILL.md</c> that participated in an index compile, reduced to the fields that decide the
/// fingerprint.
/// </summary>
/// <remarks>
/// <c>mtime</c> is intentionally absent: container images and CI checkouts rewrite timestamps
/// without changing content, and a fingerprint that flips on <c>touch</c> would trigger pointless
/// recompiles. <see cref="RelativePath"/> is root-relative (plus the source prefix) so the
/// fingerprint is stable across machines and workspaces.
/// </remarks>
public sealed record SkillFileStamp(string Source, string RelativePath, long Length, string Sha256);

/// <summary>
/// Result of a full skill directory scan: the eligible skills, everything that went wrong, and one
/// stamp per <c>SKILL.md</c> that was read.
/// </summary>
public sealed record SkillLoadResult(
    IReadOnlyList<SkillDefinition> Skills,
    IReadOnlyList<SkillDiagnostic> Diagnostics,
    IReadOnlyList<SkillFileStamp> Files)
{
    /// <summary>No skills, no diagnostics, no files.</summary>
    public static SkillLoadResult Empty { get; } = new([], [], []);
}

/// <summary>
/// Builds the stable content fingerprint for a set of skill files.
/// </summary>
public static class SkillFingerprint
{
    /// <summary>Computes <c>sha256:&lt;hex&gt;</c> over the sorted <paramref name="files"/> listing.</summary>
    public static string Compute(IEnumerable<SkillFileStamp> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files
                     .Where(static f => f is not null)
                     .OrderBy(static f => f.Source, StringComparer.Ordinal)
                     .ThenBy(static f => f.RelativePath, StringComparer.Ordinal))
        {
            builder.Append(file.Source)
                .Append('|')
                .Append(file.RelativePath)
                .Append('|')
                .Append(file.Length)
                .Append('|')
                .Append(file.Sha256)
                .Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "sha256:" + Convert.ToHexStringLower(bytes);
    }

    /// <summary>Hashes file content read from <paramref name="stream"/>.</summary>
    public static string HashContent(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));
}
