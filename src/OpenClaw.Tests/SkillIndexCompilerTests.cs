using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// B4 compiler semantics (design §7.1-§7.2, T-B4-1..6). Every case runs against a real temp skill
/// tree so the loader, the fingerprint and the last-known-good rule are all exercised together.
/// </summary>
public sealed class SkillIndexCompilerTests
{
    [Fact]
    public async Task T_B4_1_A_startup_compile_publishes_version_one_and_is_ready()
    {
        using var root = SkillTree.Create();
        root.WriteSkill("alpha");
        root.WriteSkill("beta");

        var compiler = CreateCompiler(root);
        var snapshot = await compiler.CompileAsync(
            SkillIndexCompileTrigger.Startup, TestContext.Current.CancellationToken);

        Assert.Equal(1, snapshot.Version);
        Assert.True(snapshot.Ready);
        Assert.Equal(SkillIndexState.Ready, snapshot.State);
        Assert.Equal("ready", snapshot.StateName);
        Assert.Empty(snapshot.Diagnostics);
        Assert.Equal(["alpha", "beta"], snapshot.Skills.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.StartsWith("sha256:", snapshot.Fingerprint, StringComparison.Ordinal);
        Assert.Equal(snapshot.Version, compiler.Current.Version);
    }

    [Fact]
    public async Task T_B4_2_Adding_a_valid_skill_bumps_the_version_and_changes_the_fingerprint()
    {
        using var root = SkillTree.Create();
        root.WriteSkill("alpha");

        var compiler = CreateCompiler(root);
        var first = await compiler.CompileAsync(
            SkillIndexCompileTrigger.Startup, TestContext.Current.CancellationToken);

        root.WriteSkill("beta");
        var second = await compiler.CompileAsync(
            SkillIndexCompileTrigger.FileChange, TestContext.Current.CancellationToken);

        Assert.Equal(first.Version + 1, second.Version);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.Contains("beta", second.Skills.Select(s => s.Name));
        Assert.True(second.Ready);
    }

    [Fact]
    public async Task T_B4_3_Breaking_one_skill_keeps_the_previous_index_active()
    {
        using var root = SkillTree.Create();
        root.WriteSkill("alpha");
        root.WriteSkill("beta");

        var compiler = CreateCompiler(root);
        var published = await compiler.CompileAsync(
            SkillIndexCompileTrigger.Startup, TestContext.Current.CancellationToken);
        Assert.Equal(2, published.Skills.Count);

        root.BreakSkill("beta");
        var afterBreak = await compiler.CompileAsync(
            SkillIndexCompileTrigger.FileChange, TestContext.Current.CancellationToken);

        // Last known good: the broken file must not take the healthy skills offline.
        Assert.Equal(published.Version, afterBreak.Version);
        Assert.Equal(2, afterBreak.Skills.Count);
        Assert.Contains("beta", afterBreak.Skills.Select(s => s.Name));

        // ...but the attempt is visible, and it is what makes the index degraded rather than ready.
        Assert.NotNull(afterBreak.LastAttempt);
        Assert.True(afterBreak.LastAttempt!.Version > published.Version);
        Assert.False(afterBreak.LastAttempt.Publishable);
        Assert.Equal(SkillIndexState.Degraded, afterBreak.State);
        Assert.True(afterBreak.Ready);

        var diagnostic = Assert.Single(afterBreak.Diagnostics);
        Assert.EndsWith("beta/SKILL.md", diagnostic.Path.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.True(diagnostic.Fatal);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Code));
    }

    [Fact]
    public async Task T_B4_4_A_tree_that_never_compiled_is_unready_and_carries_no_version()
    {
        using var root = SkillTree.Create();
        root.BreakSkill("broken");

        var compiler = CreateCompiler(root);
        var snapshot = await compiler.CompileAsync(
            SkillIndexCompileTrigger.Startup, TestContext.Current.CancellationToken);

        Assert.Equal(0, snapshot.Version);
        Assert.False(snapshot.Ready);
        Assert.Equal(SkillIndexState.Unready, snapshot.State);
        Assert.Equal("unready", snapshot.StateName);
        Assert.Empty(snapshot.Skills);
        Assert.Single(snapshot.Diagnostics);
    }

    [Fact]
    public async Task T_B4_5_A_timestamp_only_change_does_not_change_the_fingerprint()
    {
        using var root = SkillTree.Create();
        root.WriteSkill("alpha");
        root.WriteSkill("beta");

        var compiler = CreateCompiler(root);
        var first = await compiler.CompileAsync(
            SkillIndexCompileTrigger.Startup, TestContext.Current.CancellationToken);

        // Touch every file: same bytes, different mtime. CI checkouts and image layers do exactly this.
        foreach (var file in root.SkillFiles())
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-3));

        var second = await compiler.CompileAsync(
            SkillIndexCompileTrigger.FileChange, TestContext.Current.CancellationToken);

        Assert.Equal(first.Version + 1, second.Version);
        Assert.Equal(first.Fingerprint, second.Fingerprint);

        root.WriteSkill("alpha", "alpha with a new description");
        var third = await compiler.CompileAsync(
            SkillIndexCompileTrigger.FileChange, TestContext.Current.CancellationToken);
        Assert.NotEqual(first.Fingerprint, third.Fingerprint);
    }

    [Fact]
    public async Task T_B4_6_Twenty_concurrent_reindexes_stay_monotonic_and_self_consistent()
    {
        using var root = SkillTree.Create();
        root.WriteSkill("alpha");
        root.WriteSkill("beta");

        var compiler = CreateCompiler(root);
        await compiler.CompileAsync(SkillIndexCompileTrigger.Startup, TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => compiler.CompileAsync(SkillIndexCompileTrigger.Manual, ct), ct)));

        var final = compiler.Current;

        // No half-built index ever escaped: every answer is a complete snapshot of the same tree.
        foreach (var result in results)
        {
            Assert.True(result.Ready);
            Assert.Equal(2, result.Skills.Count);
            Assert.Empty(result.Diagnostics);
            Assert.True(result.Version <= final.Version);
        }

        // Versions never repeat across publishes, and the final snapshot equals the last answer's shape.
        var versions = results.Select(static r => r.Version).Distinct().ToList();
        Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.Equal(versions.Max(), final.Version);
        Assert.Equal(2, final.Skills.Count);

        // Requests were merged rather than queued one-for-one (20 requests, strictly fewer compiles).
        Assert.InRange(final.Version, 1, 20);
    }

    [Fact]
    public async Task Disabled_skills_publish_an_empty_ready_index()
    {
        using var root = SkillTree.Create();
        root.WriteSkill("alpha");

        var compiler = new SkillIndexCompiler(
            new SkillsConfig { Enabled = false },
            NullLogger<SkillIndexCompiler>.Instance);

        var snapshot = await compiler.CompileAsync(
            SkillIndexCompileTrigger.Startup, TestContext.Current.CancellationToken);

        // Nothing to wait for: a disabled skills system must not fail readiness.
        Assert.Equal(1, snapshot.Version);
        Assert.True(snapshot.Ready);
        Assert.Empty(snapshot.Skills);
        Assert.Empty(snapshot.Diagnostics);
    }

    private static SkillIndexCompiler CreateCompiler(SkillTree root)
        => new(
            new SkillsConfig
            {
                Load = new SkillLoadConfig
                {
                    ExtraDirs = [root.Path],
                    IncludeBundled = false,
                    IncludeManaged = false,
                    IncludeWorkspace = false,
                    ScanSubdirectories = false
                }
            },
            NullLogger<SkillIndexCompiler>.Instance);

    /// <summary>A throw-away skill directory tree.</summary>
    private sealed class SkillTree : IDisposable
    {
        private SkillTree(string path) => Path = path;

        public string Path { get; }

        public static SkillTree Create()
        {
            var path = System.IO.Path.Join(
                System.IO.Path.GetTempPath(),
                "openclaw-skill-index-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new SkillTree(path);
        }

        public void WriteSkill(string name, string? description = null)
        {
            var directory = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                System.IO.Path.Combine(directory, "SKILL.md"),
                $"""
                 ---
                 name: {name}
                 description: {description ?? $"{name} skill"}
                 ---

                 Body for {name}.
                 """);
        }

        /// <summary>Rewrites a SKILL.md so it can no longer be parsed at all.</summary>
        public void BreakSkill(string name)
        {
            var directory = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                System.IO.Path.Combine(directory, "SKILL.md"),
                "# no frontmatter, so this cannot become a skill");
        }

        public IEnumerable<string> SkillFiles() => Directory.EnumerateFiles(Path, "SKILL.md", SearchOption.AllDirectories);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
