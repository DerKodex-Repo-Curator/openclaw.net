using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// T-B7-11 documentation gate: an operator who is handed a todo list needs the kinds, the states and
/// the endpoints written down, not inferred from the code.
/// </summary>
public sealed class InboxDocumentationTests
{
    [Fact]
    public void T_B7_11_The_integration_docs_describe_endpoints_kinds_states_and_codes()
    {
        var english = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "docs", "integrations", "meta-skill-invocation-api.md"));

        foreach (var token in new[]
                 {
                     "GET /api/inbox",
                     "GET /api/inbox/stream",
                     "POST /api/inbox/{id}/ack",
                     "POST /api/inbox/{id}/snooze",
                     "POST /api/inbox/{id}/resolve",
                     "POST /api/admin/inbox/rebuild",
                     "POST /api/integration/inbox/items",
                     "event: inbox_item",
                     "event: inbox_gap",
                     "Last-Event-ID",
                     InboxKinds.Approval,
                     InboxKinds.RunPaused,
                     InboxKinds.RunFailed,
                     InboxKinds.McpDown,
                     InboxKinds.SkillDegraded,
                     InboxKinds.Notification,
                     InboxStates.Open,
                     InboxStates.Acknowledged,
                     InboxStates.Resolved,
                     InboxStates.Expired,
                     InboxResolutionCodes.Approved,
                     InboxResolutionCodes.Rejected,
                     InboxResolutionCodes.Recovered,
                     InboxResolutionCodes.AutoClosed,
                     InboxErrorCodes.Disabled,
                     InboxErrorCodes.NotFound,
                     InboxErrorCodes.BadRequest,
                     InboxErrorCodes.InvalidState,
                     InboxErrorCodes.ResourceDriven,
                     InboxErrorCodes.SnoozeInPast,
                     "inbox.read",
                     "inbox.mutate",
                     "admin.inbox.rebuild",
                     "integration.mutate.inbox",
                     "OpenClaw:Inbox:MaxPageSize",
                     "SweepInterval"
                 })
        {
            Assert.Contains(token, english, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void T_B7_11b_The_chinese_integration_page_carries_the_same_contract()
    {
        var chinese = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "docs", "zh-CN", "integrations", "meta-skill-invocation-api.md"));

        foreach (var token in new[]
                 {
                     "GET /api/inbox",
                     "/api/inbox/stream",
                     "POST /api/inbox/{id}/ack",
                     "POST /api/inbox/{id}/snooze",
                     "POST /api/inbox/{id}/resolve",
                     "POST /api/admin/inbox/rebuild",
                     "POST /api/integration/inbox/items",
                     "event: inbox_item",
                     "event: inbox_gap",
                     InboxKinds.Notification,
                     InboxStates.Acknowledged,
                     InboxErrorCodes.ResourceDriven,
                     InboxErrorCodes.SnoozeInPast,
                     "OpenClaw:Inbox:MaxPageSize"
                 })
        {
            Assert.Contains(token, chinese, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_closed_sets_are_the_contract_and_stay_closed()
    {
        // A new kind that nobody documented would be an item an operator cannot interpret.
        Assert.Equal(6, InboxKinds.All.Count);
        Assert.Equal(
            InboxKinds.All,
            [InboxKinds.Approval, InboxKinds.RunPaused, InboxKinds.RunFailed,
             InboxKinds.McpDown, InboxKinds.SkillDegraded, InboxKinds.Notification]);

        // Everything but an ingested notification is closed by its resource, so a human resolve is a lie.
        Assert.False(InboxKinds.IsResourceDriven(InboxKinds.Notification));
        Assert.All(
            InboxKinds.All.Where(kind => kind != InboxKinds.Notification),
            kind => Assert.True(InboxKinds.IsResourceDriven(kind)));

        Assert.Equal(4, InboxStates.All.Count);
        Assert.True(InboxStates.IsActive(InboxStates.Open));
        Assert.True(InboxStates.IsActive(InboxStates.Acknowledged));
        Assert.True(InboxStates.IsTerminal(InboxStates.Resolved));
        Assert.True(InboxStates.IsTerminal(InboxStates.Expired));
        Assert.Equal("active", InboxStates.ParseQuery("ACTIVE"));
        Assert.Null(InboxStates.ParseQuery("closed"));

        // Unknown vocabulary parses to nothing rather than to a default somebody has to guess.
        Assert.False(InboxSeverities.IsKnown("bogus"));
        Assert.Equal(InboxSeverities.Critical, InboxSeverities.Max("Bogus", InboxSeverities.Critical));
        Assert.Equal(InboxSeverities.Info, InboxSeverities.Max(InboxSeverities.Info, "Bogus"));
        Assert.False(InboxStates.IsKnown("archived"));
    }

    [Fact]
    public void The_per_kind_ttl_defaults_are_the_documented_ones()
    {
        var config = new InboxConfig();

        Assert.Equal(TimeSpan.FromDays(7), config.TtlFor(InboxKinds.RunFailed));
        Assert.Equal(TimeSpan.FromDays(1), config.TtlFor(InboxKinds.McpDown));
        Assert.Equal(TimeSpan.FromDays(1), config.TtlFor(InboxKinds.SkillDegraded));
        Assert.Equal(TimeSpan.FromDays(7), config.TtlFor(InboxKinds.Approval));
        Assert.Equal(TimeSpan.FromDays(7), config.TtlFor(InboxKinds.RunPaused));
        Assert.Equal(TimeSpan.FromDays(7), config.TtlFor(InboxKinds.Notification));

        // A configured override wins; a nonsensical one is ignored rather than emptying the inbox.
        config.Ttl[InboxKinds.RunFailed] = TimeSpan.FromDays(3);
        Assert.Equal(TimeSpan.FromDays(3), config.TtlFor(InboxKinds.RunFailed));

        config.Ttl[InboxKinds.McpDown] = TimeSpan.Zero;
        Assert.Equal(TimeSpan.FromDays(1), config.TtlFor(InboxKinds.McpDown));
    }

    [Fact]
    public void Normalize_clamps_values_the_projection_cannot_use()
    {
        var normalized = new InboxConfig
        {
            SweepInterval = TimeSpan.Zero,
            MaxPageSize = 0,
            Ttl = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
            {
                [InboxKinds.RunFailed] = TimeSpan.Zero,
                [InboxKinds.McpDown] = TimeSpan.FromHours(2)
            }
        }.Normalize();

        Assert.Equal(TimeSpan.FromSeconds(30), normalized.SweepInterval);
        Assert.Equal(1, normalized.MaxPageSize);

        // A nonsensical TTL is dropped, not carried along to override the built-in default.
        Assert.Equal(TimeSpan.FromDays(7), normalized.TtlFor(InboxKinds.RunFailed));
        Assert.Equal(TimeSpan.FromHours(2), normalized.TtlFor(InboxKinds.McpDown));

        Assert.Equal(1000, new InboxConfig { MaxPageSize = 100_000 }.Normalize().MaxPageSize);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(current.FullName, "docs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
