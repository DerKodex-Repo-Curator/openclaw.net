using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// A delegated result that stopped at the iteration limit must announce itself. A parent agent that
/// cannot tell "finished" from "ran out of room" reports success for half a task.
/// </summary>
public sealed class DelegationCompletenessTests
{
    [Fact]
    public void AppendNotice_AddsMarkerAndIterationCount()
    {
        var marked = DelegationCompleteness.AppendNotice("Partial findings.", 6);

        Assert.StartsWith("Partial findings.", marked, StringComparison.Ordinal);
        Assert.Contains(DelegationCompleteness.MarkerPrefix, marked, StringComparison.Ordinal);
        Assert.Contains("6 model calls", marked, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendNotice_IsIdempotent()
    {
        // A summary can pass through more than one layer; the caveat must not stack up.
        var once = DelegationCompleteness.AppendNotice("Result.", 4);
        var twice = DelegationCompleteness.AppendNotice(once, 4);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void AppendNotice_StillMarksAnEmptyResult()
    {
        var marked = DelegationCompleteness.AppendNotice("   ", 2);

        Assert.True(DelegationCompleteness.HasNotice(marked));
        Assert.StartsWith(DelegationCompleteness.MarkerPrefix, marked, StringComparison.Ordinal);
    }

    [Fact]
    public void HasNotice_IsFalseForAnOrdinaryAnswer()
        => Assert.False(DelegationCompleteness.HasNotice("All done: the report is in out/report.md."));

    [Fact]
    public void HasNotice_IsFalseForNull()
        => Assert.False(DelegationCompleteness.HasNotice(null));
}
