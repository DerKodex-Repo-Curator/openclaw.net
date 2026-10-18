using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// T-B4-9 documentation gate: the two switches and the two endpoints are the only way an operator
/// finds out why a deployment is answering <c>503 skills.not_ready</c>, so they have to stay written
/// down next to the feature.
/// </summary>
public sealed class SkillIndexDocumentationTests
{
    [Fact]
    public void T_B4_9_The_config_doc_documents_the_two_switches_and_the_endpoints()
    {
        var root = FindRepositoryRoot();
        var metaSkills = File.ReadAllText(Path.Combine(root, "docs", "meta-skills.md"));
        var integrationApi = File.ReadAllText(
            Path.Combine(root, "docs", "integrations", "meta-skill-invocation-api.md"));

        foreach (var token in new[]
                 {
                     "Skills.FailClosedOnUnreadyIndex",
                     "FailClosedOnUnreadyIndex",
                     "FailOnDegradedSkills",
                     "/api/integration/skill-index",
                     "/admin/skills/reindex",
                     "skills.not_ready",
                     SkillIndexStates.Degraded,
                     SkillIndexStates.Unready
                 })
        {
            Assert.Contains(token, metaSkills, StringComparison.Ordinal);
        }

        // The integration page describes the machine face, the header and the 503 contract.
        foreach (var token in new[]
                 {
                     "GET /api/integration/skill-index",
                     "skills.not_ready",
                     "X-OpenClaw-Skill-Index",
                     "lastAttemptVersion",
                     "/health/ready"
                 })
        {
            Assert.Contains(token, integrationApi, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_wire_states_are_named_exactly_once()
    {
        // The strings are part of the HTTP contract (probe body, header, JSON payload); changing a
        // value here without changing the docs would be a silent breaking change.
        Assert.Equal("ready", SkillIndexStates.ToWireValue(SkillIndexState.Ready));
        Assert.Equal("degraded", SkillIndexStates.ToWireValue(SkillIndexState.Degraded));
        Assert.Equal("unready", SkillIndexStates.ToWireValue(SkillIndexState.Unready));
        Assert.Equal(SkillIndexState.Degraded, SkillIndexStates.Parse("DEGRADED"));
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
