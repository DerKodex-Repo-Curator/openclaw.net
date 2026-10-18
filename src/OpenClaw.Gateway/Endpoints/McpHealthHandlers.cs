using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Gateway.Endpoints;

/// <summary>
/// B5 read surface: the MCP health snapshot, its state-change stream, and the response header that
/// tells a caller the fleet is not fully healthy.
/// </summary>
/// <remarks>
/// <para>
/// These handlers carry no authorization of their own - the mount in
/// <see cref="IntegrationEndpoints"/> does that - so a test can drive them directly against a
/// hand-built <see cref="McpServerHealthStore"/> without standing up a gateway.
/// </para>
/// <para>
/// The payload is deliberately poorer than the internal record: no tool inventory, no exception
/// text, only the closed error codes from <see cref="McpProbeErrorCodes"/> (design §8.3).
/// </para>
/// </remarks>
internal static class McpHealthHandlers
{
    /// <summary>
    /// Response header stamped on a meta-invocation when some MCP server is not healthy, so a
    /// caller can tell "the fleet is degraded" from "the skill is broken" without a second request.
    /// </summary>
    internal const string StateHeaderName = "X-OpenClaw-Mcp-State";

    internal const string HealthUnavailable = "mcp.health_unavailable";

    /// <summary>How long a stream waits for a state change before checking whether the client left.</summary>
    private const int StreamKeepAliveMilliseconds = 15_000;

    internal static McpServerHealthStore? Resolve(HttpContext ctx)
        => ctx.RequestServices.GetService<McpServerHealthStore>();

    /// <summary>
    /// Snapshot of every known server. Never fails: an unregistered store is reported as an empty
    /// fleet rather than as an error, because a caller's gate ("any down?") is still answerable.
    /// </summary>
    internal static IResult GetHealth(HttpContext ctx)
    {
        var servers = Resolve(ctx)?.Snapshot() ?? [];

        return Results.Json(
            McpHealthProjection.ToResponse(servers, DateTimeOffset.UtcNow),
            CoreJsonContext.Default.McpHealthResponse);
    }

    /// <summary>
    /// Pushes one <c>mcp_health</c> frame per state <em>change</em>. Subscribing to the store rather
    /// than polling is what makes a quiet stream mean a quiet fleet instead of a heartbeat storm.
    /// </summary>
    internal static async Task StreamAsync(HttpContext ctx)
    {
        var store = Resolve(ctx);
        if (store is null)
        {
            await Results.Json(
                new OperationStatusResponse { Success = false, Error = HealthUnavailable },
                CoreJsonContext.Default.OperationStatusResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(ctx).ConfigureAwait(false);
            return;
        }

        var cancellationToken = ctx.RequestAborted;
        var frames = Channel.CreateUnbounded<McpHealthStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        void OnChanged(McpHealthTransition transition)
        {
            var frame = new McpHealthStreamEvent
            {
                Server = McpHealthProjection.ToEntry(transition.Health),
                PreviousState = McpServerHealthStates.ToWireValue(transition.PreviousState),
                ToolsChanged = transition.ToolsChanged,
                OccurredAtUtc = transition.Health.LastProbeAtUtc ?? DateTimeOffset.UtcNow
            };

            frames.Writer.TryWrite(frame);
        }

        store.Changed += OnChanged;
        try
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            // The opening comment flushes headers, so a client knows it is connected before the
            // first server happens to change state.
            await ctx.Response.WriteAsync(": connected\n\n", cancellationToken).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(StreamKeepAliveMilliseconds);

                McpHealthStreamEvent frame;
                try
                {
                    frame = await frames.Reader.ReadAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Idle window elapsed with no change: write a comment so a proxy does not
                    // decide the connection is dead. Comments are invisible to EventSource clients.
                    await ctx.Response.WriteAsync(": keepalive\n\n", cancellationToken).ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await WriteSseAsync(
                    ctx,
                    "mcp_health",
                    JsonSerializer.Serialize(frame, CoreJsonContext.Default.McpHealthStreamEvent),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client went away: the normal end of an SSE request.
        }
        catch (IOException)
        {
            // Broken pipe: same story, and there is nobody left to tell.
        }
        finally
        {
            store.Changed -= OnChanged;
            frames.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Stamps <see cref="StateHeaderName"/> when the fleet is <c>degraded</c> or <c>down</c>.
    /// A fully healthy fleet writes nothing: a header that is always present carries no signal.
    /// </summary>
    internal static void Annotate(HttpContext ctx)
    {
        var store = Resolve(ctx);
        if (store is null || ctx.Response.HasStarted)
            return;

        var worst = WorstState(store.Snapshot());
        if (worst is McpServerHealthState.Degraded or McpServerHealthState.Down)
            ctx.Response.Headers[StateHeaderName] = McpServerHealthStates.ToWireValue(worst.Value);
    }

    /// <summary>Most severe state across the fleet; <c>null</c> when nothing is tracked.</summary>
    internal static McpServerHealthState? WorstState(IReadOnlyList<McpServerHealth> servers)
    {
        if (servers.Count == 0)
            return null;

        var worst = McpServerHealthState.Unknown;
        foreach (var server in servers)
        {
            if (server.State > worst)
                worst = server.State;
        }

        return worst;
    }

    private static async Task WriteSseAsync(
        HttpContext ctx,
        string eventName,
        string data,
        CancellationToken cancellationToken)
    {
        var frame = new StringBuilder(data.Length + 32)
            .Append("event: ").Append(eventName).Append('\n')
            .Append("data: ").Append(data).Append("\n\n");

        await ctx.Response.WriteAsync(frame.ToString(), cancellationToken).ConfigureAwait(false);
        await ctx.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
