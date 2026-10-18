using System.Threading.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Gateway.Inbox;

/// <summary>What the run ledger is allowed to tell the inbox: one finished event, and nothing else.</summary>
/// <remarks>
/// Narrow on purpose. The ledger already holds a lock while it appends, so the only safe thing it
/// can do is hand off; giving it the projector (or the store) would put an inbox write - and its
/// IO - inside the ledger's critical section.
/// </remarks>
public interface IInboxRunEventSink
{
    void Publish(MetaRunEvent runEvent);
}

/// <summary>One unit of projection work.</summary>
public abstract record InboxProjectionWork
{
    public sealed record Run(MetaRunEvent Event) : InboxProjectionWork;

    public sealed record Mcp(McpHealthTransition Transition) : InboxProjectionWork;
}

/// <summary>
/// Bounded hand-off between the producers (ledger, MCP probe) and <see cref="InboxProjector"/>.
/// </summary>
/// <remarks>
/// Drops rather than blocks when full, and counts what it dropped: the inbox is a projection, and a
/// projection that stalls a run or a probe would be trading a lost todo for a stuck system. A
/// dropped event is recovered by the next <c>rebuild</c> and by the periodic snapshot pass.
/// </remarks>
public sealed class InboxProjectionQueue : IInboxRunEventSink
{
    public const int DefaultCapacity = 2048;

    private readonly Channel<InboxProjectionWork> queue;
    private long dropped;

    public InboxProjectionQueue(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        queue = Channel.CreateBounded<InboxProjectionWork>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public long DroppedCount => Interlocked.Read(ref dropped);

    public int PendingCount => queue.Reader.Count;

    public void Publish(MetaRunEvent runEvent)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        Enqueue(new InboxProjectionWork.Run(runEvent));
    }

    public void Publish(McpHealthTransition transition) => Enqueue(new InboxProjectionWork.Mcp(transition));

    internal IAsyncEnumerable<InboxProjectionWork> ReadAllAsync(CancellationToken cancellationToken)
        => queue.Reader.ReadAllAsync(cancellationToken);

    private void Enqueue(InboxProjectionWork work)
    {
        if (queue.Writer.TryWrite(work))
            return;

        Interlocked.Increment(ref dropped);
    }
}
