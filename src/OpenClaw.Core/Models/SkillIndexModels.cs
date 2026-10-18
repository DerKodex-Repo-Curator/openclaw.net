using OpenClaw.Core.Skills;

namespace OpenClaw.Core.Models;

/// <summary>
/// One skill as reported by <c>GET /api/integration/skill-index</c>.
/// </summary>
public sealed record SkillIndexEntry
{
    public required string Name { get; init; }

    /// <summary><c>standard</c> or <c>meta</c>.</summary>
    public required string Kind { get; init; }

    /// <summary><c>bundled</c> / <c>managed</c> / <c>workspace</c> / <c>extra</c> / <c>plugin</c>.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// <c>ready</c> for every skill the runtime can serve, <c>invalid</c> for a skill directory whose
    /// <c>SKILL.md</c> failed to parse, <c>disabled</c> for a skill turned off by config.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>Diagnostic code explaining a non-ready status.</summary>
    public string? DiagnosticCode { get; init; }
}

/// <summary>One compiler diagnostic in wire form.</summary>
public sealed record SkillIndexDiagnosticResponse
{
    public required string Path { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }

    /// <summary>Whether this diagnostic prevented the index from being published.</summary>
    public bool Fatal { get; init; }
}

/// <summary>
/// Response for <c>GET /api/integration/skill-index</c>.
/// </summary>
public sealed record SkillIndexResponse
{
    /// <summary>Version of the active index; <c>0</c> when nothing usable has ever been published.</summary>
    public long Version { get; init; }

    public string Fingerprint { get; init; } = string.Empty;

    public DateTimeOffset CompiledAtUtc { get; init; }

    /// <summary>Whether the active index can serve skill-dependent traffic.</summary>
    public bool Ready { get; init; }

    /// <summary><c>unready</c> / <c>ready</c> / <c>degraded</c>.</summary>
    public required string State { get; init; }

    /// <summary>Version of the most recent compile attempt, which may be newer than <see cref="Version"/>.</summary>
    public long LastAttemptVersion { get; init; }

    public IReadOnlyList<SkillIndexEntry> Skills { get; init; } = [];

    public IReadOnlyList<SkillIndexDiagnosticResponse> Diagnostics { get; init; } = [];
}

/// <summary>
/// Response for <c>POST /admin/skills/reindex</c>.
/// </summary>
public sealed record SkillReindexResponse
{
    public long Version { get; init; }

    public long PreviousVersion { get; init; }

    public string Fingerprint { get; init; } = string.Empty;

    public bool Ready { get; init; }

    public required string State { get; init; }

    public IReadOnlyList<SkillIndexDiagnosticResponse> Diagnostics { get; init; } = [];
}

/// <summary>
/// The <c>skills</c> section of <c>GET /health/ready</c>.
/// </summary>
public sealed record SkillIndexHealth
{
    public required string State { get; init; }

    public long Version { get; init; }

    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>Number of diagnostics carried by the latest compile attempt.</summary>
    public int Diagnostics { get; init; }
}

/// <summary>
/// Maps a <see cref="SkillIndexSnapshot"/> onto the wire contracts above. Kept in Core so the
/// endpoint, the readiness probe and the tests all render the same shape.
/// </summary>
public static class SkillIndexProjection
{
    private const string StatusReady = "ready";
    private const string StatusInvalid = "invalid";
    private const string StatusDisabled = "disabled";
    private const string UnknownKind = "unknown";

    public static SkillIndexResponse ToResponse(SkillIndexSnapshot snapshot, SkillsConfig? config = null)
        => new()
        {
            Version = snapshot.Version,
            Fingerprint = snapshot.Fingerprint,
            CompiledAtUtc = snapshot.CompiledAtUtc,
            Ready = snapshot.Ready,
            State = snapshot.StateName,
            LastAttemptVersion = snapshot.LastAttempt?.Version ?? 0,
            Skills = BuildEntries(snapshot, config),
            Diagnostics = ToDiagnostics(snapshot.Diagnostics)
        };

    public static SkillIndexHealth ToHealth(SkillIndexSnapshot snapshot)
        => new()
        {
            State = snapshot.StateName,
            Version = snapshot.Version,
            Fingerprint = snapshot.Fingerprint,
            Diagnostics = snapshot.Diagnostics.Count
        };

    public static IReadOnlyList<SkillIndexDiagnosticResponse> ToDiagnostics(IReadOnlyList<SkillDiagnostic> diagnostics)
        => [.. diagnostics.Select(static d => new SkillIndexDiagnosticResponse
        {
            Path = d.Path,
            Code = d.Code,
            Message = d.Message,
            Fatal = d.Fatal
        })];

    private static IReadOnlyList<SkillIndexEntry> BuildEntries(
        SkillIndexSnapshot snapshot,
        SkillsConfig? config)
    {
        var entries = new List<SkillIndexEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var skill in snapshot.Skills.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(skill.Name))
                continue;

            entries.Add(new SkillIndexEntry
            {
                Name = skill.Name,
                Kind = skill.Kind == SkillKind.Meta ? "meta" : "standard",
                Source = skill.Source.ToString().ToLowerInvariant(),
                Status = StatusReady
            });
        }

        // A SKILL.md that failed to parse has no trustworthy name, so it is reported under its
        // directory name. This is what makes an "invalid" entry actionable next to the diagnostic.
        foreach (var diagnostic in snapshot.Diagnostics.Where(static d => d.Fatal))
        {
            var name = SkillNameFromPath(diagnostic.Path);
            if (name is null || !seen.Add(name))
                continue;

            entries.Add(new SkillIndexEntry
            {
                Name = name,
                Kind = UnknownKind,
                Source = UnknownKind,
                Status = StatusInvalid,
                DiagnosticCode = diagnostic.Code
            });
        }

        if (config?.Entries is { Count: > 0 } overrides)
        {
            foreach (var (key, entry) in overrides)
            {
                if (entry.Enabled || !seen.Add(key))
                    continue;

                entries.Add(new SkillIndexEntry
                {
                    Name = key,
                    Kind = UnknownKind,
                    Source = "config",
                    Status = StatusDisabled
                });
            }
        }

        return entries;
    }

    private static string? SkillNameFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (!string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.OrdinalIgnoreCase))
            return null;

        var directory = Path.GetDirectoryName(path);
        var name = string.IsNullOrEmpty(directory) ? null : Path.GetFileName(directory);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
