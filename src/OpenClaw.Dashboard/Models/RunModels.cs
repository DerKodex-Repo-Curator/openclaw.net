namespace OpenClaw.Dashboard.Models;

/// <summary>
/// Wire shape of one run event (B3). Hand-written rather than shared: the dashboard is a
/// WebAssembly project that links Core model files one by one, and pulling the whole
/// <c>CoreJsonContext</c> into the browser build to read four fields is not a trade worth making.
/// The server contract is pinned by <c>T-B3-11</c>, so this stays a mirror, not a second truth.
/// </summary>
public sealed class RunEventDto
{
    public Guid InvocationId { get; set; }

    /// <summary>Per-invocation sequence, starting at 1. Also the SSE frame id.</summary>
    public long Seq { get; set; }

    public DateTimeOffset AtUtc { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string? StepId { get; set; }

    public string? Status { get; set; }

    /// <summary>Short redacted label: a pause reason, an error code, a truncated approval id.</summary>
    public string? Detail { get; set; }

    /// <summary>Derived from the run record rather than observed live; its time is not a duration.</summary>
    public bool Synthetic { get; set; }
}

/// <summary>Payload of <c>GET /admin/meta-invocations/{id}/events</c>.</summary>
public sealed class RunEventPageDto
{
    public Guid InvocationId { get; set; }

    public List<RunEventDto> Events { get; set; } = [];

    public long NextSeq { get; set; }

    /// <summary>The run reached a terminal state: the timeline will not grow.</summary>
    public bool Terminal { get; set; }

    /// <summary>The journal has unreadable lines or could not be written: events may be missing.</summary>
    public bool Gap { get; set; }

    /// <summary>The whole page was derived from the run record because no journal exists.</summary>
    public bool Synthetic { get; set; }
}

/// <summary>One row of the run index.</summary>
public sealed class RunSummaryDto
{
    public Guid InvocationId { get; set; }

    public string? SessionId { get; set; }

    public string? Skill { get; set; }

    public string Status { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public string? PauseReason { get; set; }

    public string? PauseStepId { get; set; }

    public string? Error { get; set; }
}

/// <summary>Payload of <c>GET /admin/meta-invocations</c>: a keyset page.</summary>
public sealed class RunListDto
{
    public List<RunSummaryDto> Items { get; set; } = [];

    public string? NextCursor { get; set; }
}

/// <summary>Last frame of the event stream: the run is over, stop reconnecting.</summary>
public sealed class RunTerminalDto
{
    public Guid InvocationId { get; set; }

    public string Status { get; set; } = string.Empty;

    public long NextSeq { get; set; }
}
