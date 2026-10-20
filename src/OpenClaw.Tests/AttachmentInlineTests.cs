using Microsoft.Extensions.AI;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// P2 of the attachment-by-reference design: references are resolved to provider content under an
/// explicit cost policy instead of being replayed as marker text on every request.
/// </summary>
/// <remarks>
/// <para>
/// These tests are about <em>policy</em>, so most of them are really asserting that something did
/// <b>not</b> happen: no read for an over-budget file, no second copy of a repeated image, no content
/// part for a turn outside the window. <see cref="FakeByteSource"/> exists to make "no read" observable —
/// which is also why the budget check has to run before the read, not after.
/// </para>
/// <para>
/// See docs/superpowers/specs/2026-10-19-attachment-by-reference-design.md.
/// </para>
/// </remarks>
public sealed class AttachmentInlineTests
{
    [Fact]
    public async Task Only_the_most_recent_user_turns_are_inlined()
    {
        var source = new FakeByteSource();
        var session = NewSession();
        for (var i = 0; i < 4; i++)
            AddImageTurn(session, source, $"/tmp/openclaw/p{i}.png", kilobytes: 4);

        var messages = await ComposeAsync(session, new AttachmentInlinePolicy { InlineRecentUserTurns = 2 }, source);

        var userMessages = messages.Where(message => message.Role == ChatRole.User).ToList();
        Assert.Equal(4, userMessages.Count);

        Assert.Contains(userMessages[0].Contents, content => IsPlaceholder(content, "outside-window"));
        Assert.Contains(userMessages[1].Contents, content => IsPlaceholder(content, "outside-window"));
        Assert.Contains(userMessages[2].Contents, content => content is DataContent);
        Assert.Contains(userMessages[3].Contents, content => content is DataContent);
    }

    [Fact]
    public async Task Assistant_and_tool_turns_do_not_consume_window_slots()
    {
        // Window counted in history entries instead of user turns would mark only the newest user turn
        // here, because the assistant reply and the tool batch would each eat a slot.
        var source = new FakeByteSource();
        var session = NewSession();
        AddImageTurn(session, source, "/tmp/openclaw/first.png", kilobytes: 4);
        session.History.Add(new ChatTurn { Role = "assistant", Content = "looking" });
        session.History.Add(new ChatTurn
        {
            Role = "assistant",
            Content = "[tool_use]",
            ToolCalls = [new ToolInvocation { CallId = "call_1", ToolName = "demo_tool", Arguments = "{}", Result = "ok" }]
        });
        AddImageTurn(session, source, "/tmp/openclaw/second.png", kilobytes: 4);

        var messages = await ComposeAsync(session, new AttachmentInlinePolicy { InlineRecentUserTurns = 2 }, source);

        var userMessages = messages.Where(message => message.Role == ChatRole.User).ToList();
        Assert.Equal(2, userMessages.Count);
        Assert.All(userMessages, message => Assert.Contains(message.Contents, content => content is DataContent));
    }

    [Fact]
    public async Task The_byte_budget_goes_to_the_newest_turn_first()
    {
        var source = new FakeByteSource();
        var session = NewSession();
        AddImageTurn(session, source, "/tmp/openclaw/older.png", kilobytes: 15 * 1024);
        AddImageTurn(session, source, "/tmp/openclaw/newer.png", kilobytes: 15 * 1024);

        var messages = await ComposeAsync(session, new AttachmentInlinePolicy
        {
            InlineRecentUserTurns = 2,
            MaxInlineBytesPerRequest = 20L * 1024 * 1024
        }, source);

        var userMessages = messages.Where(message => message.Role == ChatRole.User).ToList();

        // The older turn loses: a follow-up question can recover "that picture again", nothing can
        // recover an answer to a question the model never saw.
        Assert.Contains(userMessages[0].Contents, content => IsPlaceholder(content, "over-byte-budget"));
        Assert.Contains(userMessages[1].Contents, content => content is DataContent);
    }

    [Fact]
    public async Task Degradation_is_per_attachment_and_follows_the_original_order()
    {
        var source = new FakeByteSource();
        var session = NewSession();
        source.Add("/tmp/openclaw/a.png", 10L * 1024 * 1024);
        source.Add("/tmp/openclaw/b.png", 10L * 1024 * 1024);
        source.Add("/tmp/openclaw/c.png", 10L * 1024 * 1024);
        session.History.Add(AttachmentReferences.CreateUserTurn(
            "three of them\n[IMAGE_PATH:/tmp/openclaw/a.png]\n[IMAGE_PATH:/tmp/openclaw/b.png]\n[IMAGE_PATH:/tmp/openclaw/c.png]"));

        var messages = await ComposeAsync(session, new AttachmentInlinePolicy
        {
            MaxInlineBytesPerRequest = 20L * 1024 * 1024
        }, source);

        var contents = Assert.Single(messages).Contents;

        // Two fit, the third does not. Sorting by size to pick "the cheapest two" would be
        // indistinguishable here, so the order assertion is what pins the rule down.
        Assert.Collection(
            contents,
            content => Assert.Equal("three of them", Assert.IsType<TextContent>(content).Text),
            content => Assert.Equal("image/png", Assert.IsType<DataContent>(content).MediaType),
            content => Assert.Equal("image/png", Assert.IsType<DataContent>(content).MediaType),
            content => Assert.Contains("over-byte-budget", Assert.IsType<TextContent>(content).Text));
    }

    [Fact]
    public async Task An_over_budget_file_is_never_read()
    {
        var source = new FakeByteSource();
        var session = NewSession();
        AddImageTurn(session, source, "/tmp/openclaw/huge.png", kilobytes: 30 * 1024);

        var messages = await ComposeAsync(session, new AttachmentInlinePolicy
        {
            MaxInlineBytesPerRequest = 20L * 1024 * 1024
        }, source);

        Assert.Contains(Assert.Single(messages).Contents, content => IsPlaceholder(content, "over-byte-budget"));
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task The_per_turn_attachment_limit_keeps_the_leading_attachments()
    {
        var source = new FakeByteSource();
        var session = NewSession();
        var paths = Enumerable.Range(0, 6).Select(i => $"/tmp/openclaw/many{i}.png").ToArray();
        foreach (var path in paths)
            source.Add(path, 1024L);

        session.History.Add(AttachmentReferences.CreateUserTurn(
            "six\n" + string.Join('\n', paths.Select(path => $"[IMAGE_PATH:{path}]"))));

        var messages = await ComposeAsync(session, new AttachmentInlinePolicy { MaxInlineAttachmentsPerTurn = 4 }, source);

        var contents = Assert.Single(messages).Contents;
        Assert.Equal(4, contents.Count(content => content is DataContent));
        Assert.Equal(2, contents.Count(content => content is TextContent text && text.Text!.Contains("over-turn-limit", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_repeated_attachment_is_sent_once_per_request()
    {
        var source = new FakeByteSource();
        var session = NewSession();
        AddImageTurn(session, source, "/tmp/openclaw/same.png", kilobytes: 4);
        AddImageTurn(session, source, "/tmp/openclaw/same.png", kilobytes: 4);

        var messages = await ComposeAsync(session, AttachmentInlinePolicy.Default, source);

        Assert.Single(messages.SelectMany(message => message.Contents).OfType<DataContent>());
        Assert.Single(messages.SelectMany(message => message.Contents), content => IsPlaceholder(content, "duplicate"));
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task A_missing_file_degrades_instead_of_failing_the_turn()
    {
        var source = new FakeByteSource();
        var session = NewSession();
        session.History.Add(AttachmentReferences.CreateUserTurn("gone\n[IMAGE_PATH:/tmp/openclaw/missing.png]"));

        var messages = await ComposeAsync(session, AttachmentInlinePolicy.Default, source);

        Assert.Contains(Assert.Single(messages).Contents, content => IsPlaceholder(content, "unavailable"));
    }

    [Fact]
    public async Task A_missing_file_fails_the_turn_when_the_policy_says_fail()
    {
        var source = new FakeByteSource();
        var session = NewSession();
        session.History.Add(AttachmentReferences.CreateUserTurn("gone\n[IMAGE_PATH:/tmp/openclaw/missing.png]"));

        var exception = await Assert.ThrowsAsync<AttachmentUnavailableException>(async () =>
            await ComposeAsync(session, new AttachmentInlinePolicy { FailureMode = "fail" }, source));

        Assert.Equal("missing.png", exception.Attachment.FileName);
    }

    [Fact]
    public async Task Local_files_are_inlined_as_bytes_so_no_file_uri_reaches_the_provider()
    {
        var source = new FakeByteSource();
        var session = NewSession();
        AddImageTurn(session, source, "/tmp/openclaw/photo.png", kilobytes: 4);

        var messages = await ComposeAsync(session, AttachmentInlinePolicy.Default, source);

        var contents = Assert.Single(messages).Contents;
        var data = Assert.Single(contents.OfType<DataContent>());
        Assert.Equal("image/png", data.MediaType);
        Assert.DoesNotContain(contents.OfType<UriContent>(), content => content.Uri?.IsFile == true);
    }

    [Fact]
    public async Task Remote_attachments_stay_uris_and_do_not_touch_the_byte_budget()
    {
        var source = new FakeByteSource();
        var session = NewSession();
        session.History.Add(AttachmentReferences.CreateUserTurn("see\n[IMAGE_URL:https://example.test/big.png]"));

        var messages = await ComposeAsync(session, new AttachmentInlinePolicy { MaxInlineBytesPerRequest = 1 }, source);

        var uri = Assert.Single(Assert.Single(messages).Contents.OfType<UriContent>());
        Assert.Equal("https://example.test/big.png", uri.Uri!.ToString());
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task The_rollback_switch_restores_marker_rendering_for_referenced_turns()
    {
        // ReferenceMode=false must reproduce the pre-P2 output exactly — including the file:// URI that
        // cloud providers cannot read. Reproducing the old defect is the point of a rollback, not a bug.
        var source = new FakeByteSource();
        var session = NewSession();
        AddImageTurn(session, source, "/tmp/openclaw/photo.png", kilobytes: 4);

        var messages = await ComposeAsync(session, AttachmentInlinePolicy.Legacy, source);

        var contents = Assert.Single(messages).Contents;
        Assert.Empty(contents.OfType<DataContent>());
        var uri = Assert.Single(contents.OfType<UriContent>());
        Assert.True(uri.Uri!.IsFile);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task Both_runtimes_compose_history_through_one_implementation()
    {
        // MafAgentRuntime cannot be instantiated here (it needs the full MAF service graph), but it now
        // calls the same composer AgentPromptContextAssembler does. Pinning the assembler to the
        // composer is therefore the checkable half of the parity claim.
        var source = new FakeByteSource();
        var session = NewSession();
        AddImageTurn(session, source, "/tmp/openclaw/photo.png", kilobytes: 4);
        session.History.Add(new ChatTurn { Role = "assistant", Content = "nice picture" });

        var composer = new ChatMessageComposer(AttachmentInlinePolicy.Default, source);
        var viaComposer = await composer.ComposeHistoryAsync(
            session, maxHistoryTurns: 50, exactLatestToolBatch: false, TestContext.Current.CancellationToken);

        var assembler = new AgentPromptContextAssembler(
            Substitute.For<IMemoryStore>(),
            requireToolApproval: false,
            recall: null,
            profileStore: null,
            profilesConfig: null,
            contextBudgetPlanner: null,
            fractalMemory: null,
            metrics: null,
            logger: null,
            memoryRecallPrefix: null,
            messageComposer: composer);
        assembler.ApplySkills([], skillsInstructionPrompt: null);

        var viaAssembler = await assembler.BuildMessagesAsync(session, maxHistoryTurns: 50);

        // The assembler owns the system preamble; everything after it must be the composer's output.
        Assert.Equal(Describe(viaComposer), Describe(viaAssembler.Skip(1)));
    }

    [Fact]
    public void Placeholder_text_names_the_reason_and_the_attachment()
    {
        var attachment = new ChatAttachmentRef
        {
            Kind = MediaMarkerKind.ImagePath,
            Value = "/tmp/openclaw/cat.jpg",
            FileName = "cat.jpg",
            SizeBytes = 3 * 1024 * 1024
        };

        var text = AttachmentInliner.Placeholder(attachment, "over-byte-budget");

        Assert.Equal("[attachment not sent (over-byte-budget): cat.jpg, 3 MB]", text);
    }

    [Fact]
    public void Placeholder_text_carries_the_asset_id_when_the_attachment_is_cached()
    {
        // Cache-backed attachments get an id so an operator can grep for it; a temp path has none, and
        // printing a made-up one would be a dead end.
        var attachment = new ChatAttachmentRef
        {
            Kind = MediaMarkerKind.ImagePath,
            Value = "/var/openclaw/media-cache/media_0123456789abcdef.jpg",
            FileName = "media_0123456789abcdef.jpg",
            AssetId = "media_0123456789abcdef"
        };

        var text = AttachmentInliner.Placeholder(attachment, "unavailable");

        Assert.Equal(
            "[attachment not sent (unavailable): media_0123456789abcdef.jpg, ref=media_0123456789abcdef]",
            text);
    }

    private static Session NewSession() => new()
    {
        Id = "sess-attachments",
        ChannelId = "websocket",
        SenderId = "user"
    };

    private static void AddImageTurn(Session session, FakeByteSource source, string path, int kilobytes)
    {
        source.Add(path, kilobytes * 1024L);
        session.History.Add(AttachmentReferences.CreateUserTurn($"look at this\n[IMAGE_PATH:{path}]"));
    }

    private static async Task<List<ChatMessage>> ComposeAsync(
        Session session,
        AttachmentInlinePolicy policy,
        IAttachmentByteSource source)
    {
        var composer = new ChatMessageComposer(policy, source);
        return await composer.ComposeHistoryAsync(
            session,
            maxHistoryTurns: 50,
            exactLatestToolBatch: false,
            TestContext.Current.CancellationToken);
    }

    private static bool IsPlaceholder(AIContent content, string reason)
        => content is TextContent text
            && text.Text is not null
            && text.Text.Contains($"[attachment not sent ({reason}):", StringComparison.Ordinal);

    private static List<string> Describe(IEnumerable<ChatMessage> messages)
    {
        var described = new List<string>();
        foreach (var message in messages)
        {
            described.Add($"{message.Role}:{string.Join(" + ", message.Contents.Select(DescribeContent))}");
        }

        return described;
    }

    private static string DescribeContent(AIContent content) => content switch
    {
        TextContent text => $"text:{text.Text}",
        DataContent data => $"data:{data.MediaType}:{data.Data.Length}",
        UriContent uri => $"uri:{uri.MediaType}:{uri.Uri}",
        _ => $"other:{content.GetType().Name}"
    };

    /// <summary>
    /// Byte source whose sizes and contents are independent, so a 30 MB budget case does not allocate
    /// 30 MB, and whose read count is observable.
    /// </summary>
    private sealed class FakeByteSource : IAttachmentByteSource
    {
        private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);

        public int Reads { get; private set; }

        public void Add(string path, long size) => _sizes[Path.GetFullPath(path)] = size;

        public ValueTask<long?> GetSizeAsync(string path, CancellationToken ct)
            => ValueTask.FromResult(_sizes.TryGetValue(Path.GetFullPath(path), out var size) ? (long?)size : null);

        public ValueTask<byte[]?> ReadAsync(string path, CancellationToken ct)
        {
            Reads++;
            if (!_sizes.ContainsKey(Path.GetFullPath(path)))
                return ValueTask.FromResult<byte[]?>(null);

            // The inliner never inspects the payload, only its media type — sizes come from
            // GetSizeAsync. A stub of fixed length keeps a 30 MB budget case from allocating 30 MB.
            return ValueTask.FromResult<byte[]?>(new byte[16]);
        }
    }
}
