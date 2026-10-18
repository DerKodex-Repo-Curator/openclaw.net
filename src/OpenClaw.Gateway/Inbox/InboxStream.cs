using System.Threading.Channels;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Inbox;

/// <summary>One published change, with the sequence a client echoes back as <c>Last-Event-ID</c>.</summary>
public sealed record InboxStreamFrame(long Seq, string Change, InboxItem Item)
{
    /// <summary>SSE <c>id:</c> value. Plain sequence, so <c>Last-Event-ID</c> parses without a rule.</summary>
    public string EventId => Seq.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Live tail of one subscriber. Disposing detaches it from the hub (idempotent).</summary>
public sealed class InboxStreamSubscription : IDisposable
{
    private readonly Channel<InboxStreamFrame> channel;
    private readonly Action<long> unsubscribe;

    internal InboxStreamSubscription(long id, Channel<InboxStreamFrame> channel, Action<long> unsubscribe)
    {
        Id = id;
        this.channel = channel;
        this.unsubscribe = unsubscribe;
    }

    internal long Id { get; }

    public ChannelReader<InboxStreamFrame> Reader => channel.Reader;

    internal ChannelWriter<InboxStreamFrame> Writer => channel.Writer;

    /// <summary>True when the requested resume point had already rolled out of the ring.</summary>
    public bool Gap { get; internal set; }

    public void Dispose() => unsubscribe(Id);
}

/// <summary>
/// Fan-out of item changes for <c>GET /api/inbox/stream</c>, with a small replay ring so a
/// reconnecting dashboard resumes with <c>Last-Event-ID</c> instead of refetching the list.
/// </summary>
/// <remarks>
/// <para>
/// The ring is deliberately small and lossy: the list endpoint is the source of truth, and a client
/// that fell too far behind is told so (<see cref="InboxStreamSubscription.Gap"/>) rather than being
/// replayed thousands of frames it would immediately discard.
/// </para>
/// <para>
/// A subscriber whose bounded buffer overflows is detached and marked as a gap rather than stalling
/// the publisher: a slow dashboard must never make an item transition wait.
/// </para>
/// </remarks>
public sealed class InboxStream
{
    public const int DefaultReplayWindow = 256;
    public const int DefaultSubscriberCapacity = 64;

    private readonly object gate = new();
    private readonly InboxStreamFrame?[] ring;
    private readonly Dictionary<long, InboxStreamSubscription> subscribers = [];
    private readonly int subscriberCapacity;
    private long lastSeq;
    private long nextSubscriptionId;

    public InboxStream(int replayWindow = DefaultReplayWindow, int subscriberCapacity = DefaultSubscriberCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(replayWindow, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(subscriberCapacity, 1);
        ring = new InboxStreamFrame?[replayWindow];
        this.subscriberCapacity = subscriberCapacity;
    }

    public int ReplayWindow => ring.Length;

    /// <summary>Highest sequence handed out. Zero means "nothing has changed yet".</summary>
    public long LastSeq
    {
        get
        {
            lock (gate)
                return lastSeq;
        }
    }

    /// <summary>Oldest sequence still replayable. 1 while the window has never rolled.</summary>
    public long FirstRetainedSeq
    {
        get
        {
            lock (gate)
                return Math.Max(1, lastSeq - ring.Length + 1);
        }
    }

    public int SubscriberCount
    {
        get
        {
            lock (gate)
                return subscribers.Count;
        }
    }

    /// <summary>Assigns the next sequence, keeps the frame for replay and pushes it to every tail.</summary>
    public InboxStreamFrame Publish(string change, InboxItem item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(change);
        ArgumentNullException.ThrowIfNull(item);

        InboxStreamFrame frame;
        List<long>? overflowed = null;

        lock (gate)
        {
            frame = new InboxStreamFrame(++lastSeq, change, item);
            ring[(int)(frame.Seq % ring.Length)] = frame;

            foreach (var (id, subscription) in subscribers)
            {
                if (!subscription.Writer.TryWrite(frame))
                    (overflowed ??= []).Add(id);
            }
        }

        if (overflowed is not null)
        {
            // Detach outside the lock; the consumer sees a completed channel and refetches.
            foreach (var id in overflowed)
            {
                if (subscribers.TryGetValue(id, out var subscription))
                    subscription.Gap = true;
                Unsubscribe(id);
            }
        }

        return frame;
    }

    /// <summary>
    /// Subscribes from <paramref name="lastEventSeq"/> (exclusive). <paramref name="gap"/> is true
    /// when the ring no longer holds everything the caller asked for.
    /// </summary>
    public InboxStreamSubscription Subscribe(long? lastEventSeq, out bool gap)
    {
        InboxStreamSubscription subscription;
        List<InboxStreamFrame> replay = [];

        lock (gate)
        {
            var id = ++nextSubscriptionId;
            var channel = Channel.CreateBounded<InboxStreamFrame>(new BoundedChannelOptions(subscriberCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

            var firstRetained = Math.Max(1, lastSeq - ring.Length + 1);
            gap = lastEventSeq is { } requested && requested < firstRetained - 1;

            if (lastEventSeq is { } after)
            {
                foreach (var frame in ring)
                {
                    if (frame is not null && frame.Seq > after)
                        replay.Add(frame);
                }
            }

            subscription = new InboxStreamSubscription(id, channel, Unsubscribe) { Gap = gap };
            subscribers[id] = subscription;
        }

        foreach (var frame in replay)
            subscription.Writer.TryWrite(frame);

        return subscription;
    }

    internal void Unsubscribe(long subscriptionId)
    {
        InboxStreamSubscription? subscription;
        lock (gate)
        {
            if (!subscribers.Remove(subscriptionId, out subscription))
                return;
        }

        subscription.Writer.TryComplete();
    }
}
