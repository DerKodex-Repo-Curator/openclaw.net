using OpenClaw.Core.Streaming;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// T-B3-15. The parser is deliberately the only piece of B3 that has no I/O, so it is the cheapest
/// place to pin the SSE edge cases a dashboard actually meets: chunk boundaries, CRLF, comments, and
/// fields it has never heard of.
/// </summary>
public sealed class SseFrameParserTests
{
    [Fact]
    public void T_B3_15_Parses_event_id_and_multiline_data()
    {
        var parser = new SseFrameParser();

        var frames = parser.Feed("event: run_event\nid: 7\ndata: {\"a\":1}\ndata: {\"b\":2}\n\n");

        var frame = Assert.Single(frames);
        Assert.Equal("run_event", frame.EventType);
        Assert.Equal("7", frame.Id);
        Assert.Equal("{\"a\":1}\n{\"b\":2}", frame.Data);
        Assert.Equal("7", parser.LastEventId);
    }

    [Fact]
    public void T_B3_15_Reassembles_a_frame_split_across_chunks()
    {
        var parser = new SseFrameParser();

        Assert.Empty(parser.Feed("event: run_event\nda"));
        Assert.Empty(parser.Feed("ta: {\"seq\":"));
        var frames = parser.Feed("3}\n\n");

        var frame = Assert.Single(frames);
        Assert.Equal("run_event", frame.EventType);
        Assert.Equal("{\"seq\":3}", frame.Data);
    }

    [Fact]
    public void T_B3_15_Handles_crlf_without_emitting_phantom_frames()
    {
        var parser = new SseFrameParser();

        var frames = parser.Feed("event: terminal\r\ndata: {}\r\n\r\n");

        var frame = Assert.Single(frames);
        Assert.Equal("terminal", frame.EventType);
        Assert.Equal("{}", frame.Data);
    }

    [Fact]
    public void T_B3_15_Ignores_comments_and_unknown_fields_but_keeps_retry()
    {
        var parser = new SseFrameParser();

        var frames = parser.Feed(": keep-alive\nretry: 2500\nx-unknown: whatever\nevent: ping\ndata: hi\n\n");

        var frame = Assert.Single(frames);
        Assert.Equal("ping", frame.EventType);
        Assert.Equal("hi", frame.Data);
        Assert.Equal(2500, frame.RetryMilliseconds);
    }

    [Fact]
    public void T_B3_15_A_block_without_data_dispatches_nothing()
    {
        var parser = new SseFrameParser();

        Assert.Empty(parser.Feed("id: 42\n\n"));
        Assert.Empty(parser.Feed("event: run_event\n\n"));

        // The id is still remembered: it is connection state, not a frame.
        Assert.Equal("42", parser.LastEventId);
    }

    [Fact]
    public void T_B3_15_Flush_returns_a_frame_that_arrived_without_its_blank_line()
    {
        var parser = new SseFrameParser();

        Assert.Empty(parser.Feed("event: run_event\ndata: last"));
        var frame = parser.Flush();

        Assert.NotNull(frame);
        Assert.Equal("run_event", frame!.EventType);
        Assert.Equal("last", frame.Data);
        Assert.Null(parser.Flush());
    }

    [Fact]
    public void T_B3_15_Two_frames_in_one_chunk_are_both_dispatched_in_order()
    {
        var parser = new SseFrameParser();

        var frames = parser.Feed("data: one\n\ndata: two\n\n");

        Assert.Equal(["one", "two"], frames.Select(frame => frame.Data));
        Assert.All(frames, frame => Assert.Equal(SseFrame.DefaultEventType, frame.EventType));
    }
}
