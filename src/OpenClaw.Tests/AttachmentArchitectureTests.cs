using System.Text;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Pins the layering that attachment handling depends on, so the next edit cannot quietly reintroduce
/// the divergence that P1–P3 removed.
/// </summary>
/// <remarks>
/// <para>
/// These read source files rather than assemblies, for the same reason
/// <see cref="ArchitectureBoundaryTests"/> does: the violation is a call someone wrote, and the file is
/// the earliest place it is visible. A behavioural test can only prove that <em>today's</em> code
/// agrees; these assert that there is only one place to disagree in.
/// </para>
/// <para>
/// Comments are stripped before matching. That is not cosmetic: the XML doc on
/// <c>MafAgentRuntime.BuildMessagesAsync</c> deliberately names the old
/// <c>AttachmentReferences.RenderMarkerText</c> call it replaced, and an architecture guard that cannot
/// tell prose from code would forbid documenting the bug it exists to prevent.
/// </para>
/// </remarks>
public sealed class AttachmentArchitectureTests
{
    /// <summary>
    /// Marker → <c>AIContent</c> conversion happens in the composer and nowhere else.
    /// </summary>
    /// <remarks>
    /// Before P2 the same logic existed twice — once live in <c>AgentPromptContextAssembler</c>, once
    /// dead in <c>AgentRuntime</c> — and a fix applied to the dead copy compiled, passed tests, and did
    /// nothing. A second call site is how that starts again.
    /// </remarks>
    [Fact]
    public void Only_the_composer_converts_a_turn_to_content()
    {
        var offenders = ProductionSources("OpenClaw.Agent")
            .Concat(ProductionSources("OpenClaw.MicrosoftAgentFrameworkAdapter"))
            .Concat(ProductionSources("OpenClaw.Gateway"))
            .Where(path => !IsFile(path, "ChatMessageComposer.cs"))
            .Where(path => ReadCode(path).Contains("AttachmentContentRenderer.Build", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Only ChatMessageComposer may call AttachmentContentRenderer.Build, but these files do: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// Neither runtime may build history messages itself, and both must reach the composer.
    /// </summary>
    /// <remarks>
    /// The MAF side used to turn every turn into a plain string via <c>RenderMarkerText</c>, which meant
    /// a user's image reached the model as the literal text <c>[IMAGE_URL:https://…]</c>. Nothing
    /// failed; the picture was simply never seen. That is the failure mode this test forbids by name.
    /// </remarks>
    [Fact]
    public void Both_runtimes_reach_the_shared_composer()
    {
        var agentAssembler = ReadCode(ProjectFile("OpenClaw.Agent", "AgentPromptContextAssembler.cs"));
        var agentRuntime = ReadCode(ProjectFile("OpenClaw.Agent", "AgentRuntime.cs"));
        var mafRuntime = ReadCode(ProjectFile("OpenClaw.MicrosoftAgentFrameworkAdapter", "MafAgentRuntime.cs"));

        Assert.Contains("ComposeHistoryAsync", agentAssembler, StringComparison.Ordinal);
        Assert.Contains("_contextAssembler.BuildMessagesAsync", agentRuntime, StringComparison.Ordinal);
        Assert.Contains("ComposeHistoryAsync", mafRuntime, StringComparison.Ordinal);

        foreach (var (name, code) in new[]
                 {
                     ("AgentRuntime", agentRuntime),
                     ("MafAgentRuntime", mafRuntime)
                 })
        {
            Assert.False(
                code.Contains("AttachmentContentRenderer.Build", StringComparison.Ordinal),
                $"{name} must not convert turns itself; that belongs to ChatMessageComposer.");
            Assert.False(
                code.Contains("RenderMarkerText", StringComparison.Ordinal),
                $"{name} must not render attachments as marker text; that belongs to ChatMessageComposer.");
        }
    }

    /// <summary>
    /// The inliner judges attachments, it does not decide which turns exist — that is the composer's
    /// window logic.
    /// </summary>
    /// <remarks>
    /// The inliner takes a single <c>inWindow</c> flag rather than history indices precisely so it
    /// cannot grow a second opinion about the window. Two window definitions would drift the moment
    /// either side is tuned.
    /// </remarks>
    [Fact]
    public void The_inliner_never_walks_session_history()
    {
        var code = ReadCode(ProjectFile("OpenClaw.Agent", "AttachmentInliner.cs"));

        Assert.DoesNotContain("History", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// A media URI is materialised into bytes in exactly one place.
    /// </summary>
    /// <remarks>
    /// Each provider client used to open attachment files itself. That is how a <c>file:</c> URI
    /// reached providers that cannot fetch it — one client read the file, another forwarded the path
    /// to a server that could not resolve it. <c>LocalAttachmentFile</c> is now the only reader; the
    /// payload adapter formats what it returns and the inbound persister copies it into the cache.
    /// </remarks>
    [Fact]
    public void Only_the_local_attachment_reader_opens_attachment_files()
    {
        var extensionsDirectory = Path.Combine(RepositoryLayout.Root, "src", "OpenClaw.Gateway", "Extensions");

        var offenders = Directory
            .EnumerateFiles(extensionsDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Where(path => !IsFile(path, "LocalAttachmentFile.cs"))
            .Where(path =>
            {
                var code = ReadCode(path);
                return code.Contains("File.ReadAllBytes(", StringComparison.Ordinal)
                       || code.Contains("File.ReadAllBytesAsync(", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Local attachment files must be read only by LocalAttachmentFile, but these files read them: " +
            string.Join(", ", offenders));
    }

    private static string ProjectFile(string project, string fileName)
    {
        var matches = Directory
            .EnumerateFiles(Path.Combine(RepositoryLayout.Root, "src", project), fileName, SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .ToArray();

        Assert.True(matches.Length == 1, $"Expected exactly one {fileName} in {project}, found {matches.Length}.");
        return matches[0];
    }

    private static IEnumerable<string> ProductionSources(string project)
        => Directory
            .EnumerateFiles(Path.Combine(RepositoryLayout.Root, "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path));

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
           || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static bool IsFile(string path, string fileName)
        => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase);

    private static string ReadCode(string path) => StripComments(File.ReadAllText(path));

    /// <summary>
    /// Removes <c>//</c> and <c>/* */</c> comments so that prose cannot trip a rule.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately blind: comment markers are stripped even when they appear inside a string literal,
    /// so a URL like <c>"https://example.test/a.png"</c> keeps only its prefix. That is safe here
    /// because the tokens these rules look for are call expressions and identifiers — none of them
    /// contains <c>//</c> — while the alternative (a real lexer) has to handle raw string literals,
    /// interpolated strings and verbatim escaping, and gets the ordering wrong in exactly the case this
    /// guard exists for: an XML doc line like <c>/// &lt;see cref="…RenderMarkerText"/&gt;</c> starts a
    /// fake string literal at the first quote and swallows the token it is supposed to ignore.
    /// </para>
    /// <para>
    /// Failure mode of being blind is a missed violation, never a false one. That is the right
    /// direction for a guard: a false alarm gets deleted or suppressed, a missed token gets fixed when
    /// someone reads the file anyway.
    /// </para>
    /// </remarks>
    private static string StripComments(string source)
    {
        var builder = new StringBuilder(source.Length);
        var index = 0;

        while (index < source.Length)
        {
            var current = source[index];

            if (current == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] != '\n')
                    index++;
                continue;
            }

            if (current == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == '/'))
                    index++;
                index = Math.Min(index + 2, source.Length);
                continue;
            }

            builder.Append(current);
            index++;
        }

        return builder.ToString();
    }
}
