using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Models;

namespace OpenClaw.Gateway;

/// <summary>
/// Append-only journal of run events, one NDJSON file per invocation next to the ledger:
/// <c>{storage}/meta-invocations/{invocationId}.events.ndjson</c>.
/// </summary>
/// <remarks>
/// <para>
/// Writes happen inside the ledger's lock (see <see cref="MetaInvocationStore"/>), so an event and
/// the state transition it describes are ordered together: there is no window where the ledger says
/// "Completed" and the journal has no <c>completed</c> event.
/// </para>
/// <para>
/// Reads are deliberately tolerant. A crash can leave a half-written last line, and an operator can
/// truncate the file by accident; neither may turn into a 500, because the timeline is exactly what
/// someone is looking at when they suspect the run is broken. Unreadable lines are skipped and
/// reported as a gap.
/// </para>
/// </remarks>
public sealed class MetaRunEventLog
{
    private readonly string _directory;

    public MetaRunEventLog(string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        _directory = Path.Combine(storagePath, "meta-invocations");
    }

    public string PathFor(Guid invocationId)
        => Path.Combine(_directory, $"{invocationId:N}.events.ndjson");

    public bool Exists(Guid invocationId) => File.Exists(PathFor(invocationId));

    /// <summary>Highest sequence already stored, or 0 when the file is absent or empty.</summary>
    public long ReadLastSeq(Guid invocationId)
    {
        var events = Read(invocationId).Events;
        return events.Count == 0 ? 0 : events[^1].Seq;
    }

    /// <summary>
    /// Appends <paramref name="drafts"/> in order, assigning consecutive sequence numbers after the
    /// last stored one. Returns the highest sequence written (0 when nothing was written).
    /// </summary>
    public async Task<long> AppendAsync(
        Guid invocationId,
        IReadOnlyList<MetaRunEventDraft> drafts,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count == 0)
            return ReadLastSeq(invocationId);

        Directory.CreateDirectory(_directory);
        var seq = ReadLastSeq(invocationId);
        var builder = new StringBuilder(drafts.Count * 192);
        foreach (var draft in drafts)
        {
            seq++;
            var payload = new MetaRunEvent(
                invocationId,
                seq,
                atUtc,
                draft.Kind,
                Truncate(draft.StepId),
                draft.Status,
                Truncate(draft.Detail),
                draft.Synthetic);
            builder.Append(JsonSerializer.Serialize(payload, CoreJsonContext.Default.MetaRunEvent))
                .Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var options = new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.Read
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        await using (var stream = new FileStream(PathFor(invocationId), options))
        {
            // One buffer, one write: a reader never sees half a record between two events, and a
            // crash truncates at a line boundary at worst.
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        return seq;
    }

    /// <summary>Reads every event of one invocation, skipping lines that do not parse.</summary>
    public MetaRunEventReadResult Read(Guid invocationId)
    {
        var path = PathFor(invocationId);
        if (!File.Exists(path))
            return new MetaRunEventReadResult([], 0);

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return new MetaRunEventReadResult([], 0);
        }
        catch (UnauthorizedAccessException)
        {
            return new MetaRunEventReadResult([], 0);
        }

        var events = new List<MetaRunEvent>(lines.Length);
        var unreadable = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;

            try
            {
                var parsed = JsonSerializer.Deserialize(line, CoreJsonContext.Default.MetaRunEvent);
                if (parsed is null || parsed.Seq <= 0 || !MetaRunEventKinds.IsKnown(parsed.Kind))
                {
                    unreadable++;
                    continue;
                }

                events.Add(parsed);
            }
            catch (JsonException)
            {
                unreadable++;
            }
        }

        events.Sort(static (left, right) => left.Seq.CompareTo(right.Seq));
        return new MetaRunEventReadResult(events, unreadable);
    }

    /// <summary>Removes the journal of a run whose ledger record was just dropped by retention.</summary>
    public bool Delete(Guid invocationId)
    {
        var path = PathFor(invocationId);
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return value.Length <= MetaRunEvent.MaxDetailLength
            ? value
            : value[..MetaRunEvent.MaxDetailLength];
    }
}
