using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Models;

/// <summary>
/// One event about to be appended. The log assigns <c>seq</c> and the timestamp; a caller supplies
/// only what it observed, so no caller can invent a sequence number or a time.
/// </summary>
public sealed record MetaRunEventDraft(
    string Kind,
    string? StepId = null,
    string? Status = null,
    string? Detail = null,
    bool Synthetic = false);

/// <summary>What one read of an event file produced, including how much of it was unreadable.</summary>
public sealed record MetaRunEventReadResult(IReadOnlyList<MetaRunEvent> Events, int UnreadableLines);
