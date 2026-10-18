using System.Text;

namespace OpenClaw.Core.Streaming;

/// <summary>
/// One dispatched SSE frame. <paramref name="Data"/> keeps the newlines of a multi-line
/// <c>data:</c> field, which is what the spec says a consumer receives.
/// </summary>
public sealed record SseFrame(
    string EventType,
    string Data,
    string? Id,
    int? RetryMilliseconds)
{
    public const string DefaultEventType = "message";
}

/// <summary>
/// Incremental SSE parser: feed it decoded text as it arrives, get back the frames that completed.
/// </summary>
/// <remarks>
/// <para>
/// Pure and dependency-free by design (design §6.5.3): it lives in Core so the Gateway and the
/// Blazor dashboard share one implementation, and so it can be unit-tested without a browser and
/// without a bUnit harness.
/// </para>
/// <para>
/// It handles what the existing <c>EventStreamService</c> does not: <c>id:</c>, <c>retry:</c>,
/// comment lines, and frames split across chunk boundaries. Unknown fields are ignored rather than
/// treated as errors, because a field added by a newer server must not break an older client.
/// </para>
/// </remarks>
public sealed class SseFrameParser
{
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _data = new();
    private string _eventType = SseFrame.DefaultEventType;
    private string? _lastEventId;
    private int? _retryMilliseconds;
    private bool _hasData;
    private bool _pendingCarriageReturn;

    /// <summary>Accumulated <c>retry:</c> hint, if the server sent one.</summary>
    public int? RetryMilliseconds => _retryMilliseconds;

    /// <summary>Last <c>id:</c> seen: what a reconnect must replay as <c>Last-Event-ID</c>.</summary>
    public string? LastEventId => _lastEventId;

    /// <summary>Feeds decoded text and returns every frame that completed during this chunk.</summary>
    public IReadOnlyList<SseFrame> Feed(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Length == 0)
            return [];

        List<SseFrame>? frames = null;
        foreach (var character in chunk)
        {
            switch (character)
            {
                case '\r':
                    // Handle CR as a line end and swallow the LF of a CRLF pair, so a CRLF stream
                    // does not produce an extra blank line between every frame.
                    _pendingCarriageReturn = true;
                    CompleteLine(ref frames);
                    break;
                case '\n':
                    if (_pendingCarriageReturn)
                    {
                        _pendingCarriageReturn = false;
                        break;
                    }

                    CompleteLine(ref frames);
                    break;
                default:
                    _pendingCarriageReturn = false;
                    _line.Append(character);
                    break;
            }
        }

        return frames ?? (IReadOnlyList<SseFrame>)[];
    }

    /// <summary>
    /// Flushes a trailing frame that arrived without its terminating blank line, so a server that
    /// closes the connection right after a frame does not lose it.
    /// </summary>
    public SseFrame? Flush()
    {
        if (_line.Length > 0)
        {
            // Completing a line only parses a field; it cannot dispatch, so nothing is lost here.
            List<SseFrame>? ignored = null;
            CompleteLine(ref ignored);
        }

        return TakeFrame();
    }

    private void CompleteLine(ref List<SseFrame>? frames)
    {
        if (_line.Length == 0)
        {
            if (TakeFrame() is { } frame)
                (frames ??= []).Add(frame);
            return;
        }

        var line = _line.ToString();
        _line.Clear();

        // Comment lines start with ':' and are keep-alives; the spec says to ignore them.
        if (line[0] == ':')
            return;

        var separator = line.IndexOf(':');
        var field = separator < 0 ? line : line[..separator];
        var value = separator < 0 ? string.Empty : line[(separator + 1)..];
        if (value.Length > 0 && value[0] == ' ')
            value = value[1..];

        switch (field)
        {
            case "event":
                _eventType = value.Length == 0 ? SseFrame.DefaultEventType : value;
                break;
            case "data":
                if (_hasData)
                    _data.Append('\n');
                _data.Append(value);
                _hasData = true;
                break;
            case "id":
                // A NUL in id is the spec's "ignore this field" marker.
                if (!value.Contains('\0', StringComparison.Ordinal))
                    _lastEventId = value;
                break;
            case "retry":
                if (int.TryParse(value, out var retry) && retry >= 0)
                    _retryMilliseconds = retry;
                break;
            default:
                // Unknown field: ignored on purpose.
                break;
        }
    }

    private SseFrame? TakeFrame()
    {
        // Per the spec a block without data dispatches nothing: id/retry alone only update state.
        if (!_hasData)
        {
            _eventType = SseFrame.DefaultEventType;
            return null;
        }

        var frame = new SseFrame(_eventType, _data.ToString(), _lastEventId, _retryMilliseconds);
        _data.Clear();
        _hasData = false;
        _eventType = SseFrame.DefaultEventType;
        return frame;
    }
}
