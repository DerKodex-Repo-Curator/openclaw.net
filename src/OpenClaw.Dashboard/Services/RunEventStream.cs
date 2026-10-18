using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Streaming;
using OpenClaw.Dashboard.Models;

namespace OpenClaw.Dashboard.Services;

/// <summary>
/// Live tail of one run's event journal, over the operator face's SSE endpoint (B3, design §6.5.3).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="EventStreamService"/> instead of extending it: that one is
/// consumed by the WhatsApp page, understands only <c>event:</c>/<c>data:</c>, and has no notion of
/// <c>id:</c> or of a terminal frame — which is exactly what a resumable timeline needs. Sharing it
/// would mean changing the semantics under an existing page.
/// </para>
/// <para>
/// The connection is re-established from the last <c>seq</c> actually received, so a dropped
/// connection neither repeats nor skips an event. A <c>terminal</c> frame, or a refused request,
/// ends the loop: reconnecting to a finished run would just burn requests forever.
/// </para>
/// </remarks>
public sealed class RunEventStream : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Backoff ceiling for a gateway that keeps closing the stream without a terminal.</summary>
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public RunEventStream(HttpClient http)
    {
        _http = http;
    }

    public bool IsConnected { get; private set; }

    /// <summary>Highest <c>seq</c> received so far; the reconnect cursor.</summary>
    public long LastSeq { get; private set; }

    /// <summary>
    /// Tails the run from <paramref name="after"/> (exclusive). Returns as soon as the loop is
    /// started: callers get events through the callbacks, not through the returned task.
    /// </summary>
    public Task StartAsync(
        Guid invocationId,
        long after,
        Func<RunEventDto, Task> onEvent,
        Func<RunTerminalDto, Task> onTerminal,
        Func<bool, Task>? onConnectionChanged = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        ArgumentNullException.ThrowIfNull(onTerminal);

        LastSeq = after;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(
            () => TailAsync(invocationId, onEvent, onTerminal, onConnectionChanged, _cts.Token),
            CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;

        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already stopped.
            }
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on stop.
            }
        }

        cts?.Dispose();
        IsConnected = false;
    }

    private async Task TailAsync(
        Guid invocationId,
        Func<RunEventDto, Task> onEvent,
        Func<RunTerminalDto, Task> onTerminal,
        Func<bool, Task>? onConnectionChanged,
        CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            StreamStop stop;
            try
            {
                stop = await ReadOnceAsync(
                        invocationId, onEvent, onTerminal, onConnectionChanged, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException)
            {
                stop = StreamStop.Closed;
            }
            catch (IOException)
            {
                stop = StreamStop.Closed;
            }

            if (stop is StreamStop.Terminal or StreamStop.Refused)
            {
                return;
            }

            // A closed stream without a terminal frame means the run is still going and something
            // on the way broke: reconnect from LastSeq, backing off so a dead gateway is not hammered.
            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = backoff >= MaxBackoff ? MaxBackoff : backoff * 2;
        }
    }

    private async Task<StreamStop> ReadOnceAsync(
        Guid invocationId,
        Func<RunEventDto, Task> onEvent,
        Func<RunTerminalDto, Task> onTerminal,
        Func<bool, Task>? onConnectionChanged,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/meta-invocations/{invocationId:D}/events/stream?after={LastSeq.ToString(CultureInfo.InvariantCulture)}");
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (LastSeq > 0)
        {
            // Belt and braces: the query cursor and the header describe the same position, and the
            // server prefers the header, so a reconnect cannot silently replay from zero.
            request.Headers.TryAddWithoutValidation(
                "Last-Event-ID", LastSeq.ToString(CultureInfo.InvariantCulture));
        }

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            await SetConnectedAsync(onConnectionChanged, false).ConfigureAwait(false);
            return StreamStop.Refused;
        }

        await SetConnectedAsync(onConnectionChanged, true).ConfigureAwait(false);

        var parser = new SseFrameParser();
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                await SetConnectedAsync(onConnectionChanged, false).ConfigureAwait(false);
                return StreamStop.Closed;
            }

            // ReadLine already dropped the line ending; feeding one back keeps the parser's
            // frame-boundary logic (blank line dispatches) intact.
            foreach (var frame in parser.Feed(line + "\n"))
            {
                if (string.Equals(frame.EventType, "run_event", StringComparison.Ordinal))
                {
                    var runEvent = JsonSerializer.Deserialize<RunEventDto>(frame.Data, JsonOptions);
                    if (runEvent is null)
                    {
                        continue;
                    }

                    LastSeq = Math.Max(LastSeq, runEvent.Seq);
                    await onEvent(runEvent).ConfigureAwait(false);
                }
                else if (string.Equals(frame.EventType, "terminal", StringComparison.Ordinal))
                {
                    var terminal = JsonSerializer.Deserialize<RunTerminalDto>(frame.Data, JsonOptions);
                    if (terminal is not null)
                    {
                        await onTerminal(terminal).ConfigureAwait(false);
                    }

                    await SetConnectedAsync(onConnectionChanged, false).ConfigureAwait(false);
                    return StreamStop.Terminal;
                }
            }
        }

        await SetConnectedAsync(onConnectionChanged, false).ConfigureAwait(false);
        return StreamStop.Closed;
    }

    private async Task SetConnectedAsync(Func<bool, Task>? onConnectionChanged, bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        if (onConnectionChanged is not null)
        {
            await onConnectionChanged(connected).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private enum StreamStop
    {
        /// <summary>Server closed the stream and the run is not over: reconnect.</summary>
        Closed,

        /// <summary>A terminal frame arrived: the timeline is complete.</summary>
        Terminal,

        /// <summary>The request was refused (401/404): retrying cannot help.</summary>
        Refused
    }
}
