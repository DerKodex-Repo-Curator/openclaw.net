using System.Text.Json;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Documentation and locale gates for B3 (design §16.2 items 3 and 6): the read endpoints, the
/// event vocabulary and the dashboard page all have to stay described where an operator looks, and
/// both locales have to carry the same keys — a missing key renders as a raw key in the UI.
/// </summary>
public sealed class MetaRunEventDocumentationTests
{
    private static readonly string[] DocumentedPaths =
    [
        "GET /api/integration/meta-invocations?",
        "GET /api/integration/meta-invocations/{invocationId}/events?",
        "GET /api/integration/meta-invocations/{invocationId}/events/stream?"
    ];

    private static readonly string[] DocumentedTokens =
    [
        "Last-Event-ID",
        "events.after_invalid",
        "runs.cursor_invalid",
        "invocation.forbidden",
        "invocation.not_found",
        "synthetic",
        "gap",
        "nextSeq",
        "/runs"
    ];

    [Fact]
    public void T_B3_17_The_integration_doc_describes_the_read_surface()
    {
        var documentation = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "docs", "integrations", "meta-skill-invocation-api.md"));

        foreach (var path in DocumentedPaths)
            Assert.Contains(path, documentation, StringComparison.Ordinal);

        foreach (var token in DocumentedTokens)
            Assert.Contains(token, documentation, StringComparison.Ordinal);

        // Every kind the gateway can write is named in the doc, so a new kind cannot quietly
        // ship without the contract page being updated.
        foreach (var kind in AllKinds())
            Assert.Contains($"`{kind}`", documentation, StringComparison.Ordinal);

        // The read surface is read-only: no mutation of a run is documented next to it.
        Assert.Contains("read-only", documentation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T_B3_17_Both_locales_carry_the_same_runs_keys()
    {
        var root = FindRepositoryRoot();
        var chinese = ReadLocale(Path.Combine(root, "src", "OpenClaw.Dashboard", "wwwroot", "locales", "zh-CN.json"));
        var english = ReadLocale(Path.Combine(root, "src", "OpenClaw.Dashboard", "wwwroot", "locales", "en-US.json"));

        var runsKeys = chinese.Keys
            .Where(key => key.StartsWith("runs.", StringComparison.Ordinal) || key == "nav.runs")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Contains("nav.runs", runsKeys);
        Assert.Contains("runs.title", runsKeys);
        Assert.Contains("runs.kind.step.paused", runsKeys);
        Assert.Equal(runsKeys, english.Keys
            .Where(key => key.StartsWith("runs.", StringComparison.Ordinal) || key == "nav.runs")
            .Order(StringComparer.Ordinal));

        // A value that is only the key itself would render as a raw key in the UI.
        foreach (var key in runsKeys)
        {
            Assert.NotEqual(key, chinese[key]);
            Assert.NotEqual(key, english[key]);
            Assert.False(string.IsNullOrWhiteSpace(chinese[key]));
            Assert.False(string.IsNullOrWhiteSpace(english[key]));
        }
    }

    private static IEnumerable<string> AllKinds()
    {
        yield return MetaRunEventKinds.Claim;
        yield return MetaRunEventKinds.ResumeRequested;
        yield return MetaRunEventKinds.Resumed;
        yield return MetaRunEventKinds.StepStarted;
        yield return MetaRunEventKinds.StepCompleted;
        yield return MetaRunEventKinds.StepFailed;
        yield return MetaRunEventKinds.StepPaused;
        yield return MetaRunEventKinds.ApprovalRequested;
        yield return MetaRunEventKinds.ApprovalDecided;
        yield return MetaRunEventKinds.Completed;
        yield return MetaRunEventKinds.Failed;
        yield return MetaRunEventKinds.Uncertain;
    }

    /// <summary>Flattens a locale file the same way the dashboard's LocalizationService does.</summary>
    private static Dictionary<string, string> ReadLocale(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Flatten(document.RootElement, string.Empty, result);
        return result;
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(
                        property.Value,
                        string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}",
                        result);
                }

                break;

            case JsonValueKind.String:
                result[prefix] = element.GetString() ?? string.Empty;
                break;

            default:
                result[prefix] = element.GetRawText();
                break;
        }
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
