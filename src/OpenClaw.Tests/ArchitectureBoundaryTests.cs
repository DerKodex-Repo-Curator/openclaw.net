using System.Xml.Linq;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Pins the layering rules that <c>docs/ARCHITECTURE_BOUNDARIES.md</c> asks contributors to respect.
/// </summary>
/// <remarks>
/// <para>
/// A rule that lives only in prose is broken by the first "just add this using" and nobody finds out
/// until an adapter drags a vendor SDK into the AOT build. These assertions turn those rules into
/// build failures at the exact line that breaks them.
/// </para>
/// <para>
/// The checks read <c>.csproj</c> files rather than assemblies: the violation is a reference that
/// someone added, so the project file is both the earliest and the most legible place to catch it.
/// Reading files means they need no project reference of their own, which is why they live in the
/// ordinary test project instead of a dedicated one — a guard against dependency creep should not
/// itself add a dependency edge to the graph it is guarding.
/// </para>
/// </remarks>
public sealed class ArchitectureBoundaryTests
{
    /// <summary>
    /// <c>OpenClaw.Core</c> is the shared vocabulary. Composing it out of product projects would make
    /// every layer depend on every other layer in a single edit.
    /// </summary>
    [Fact]
    public void Core_ReferencesNoOtherProductProject()
    {
        var references = RepositoryLayout.ProjectReferences(RepositoryLayout.CoreProject);

        Assert.True(
            references.Count == 0,
            "OpenClaw.Core must not reference another product project, but references: " +
            string.Join(", ", references));
    }

    /// <summary>
    /// Core may depend on <c>Microsoft.Extensions.AI.Abstractions</c> (interfaces only) but never on a
    /// provider SDK. This is the rule that keeps the AOT lane and the optional-adapter builds working.
    /// </summary>
    [Fact]
    public void Core_ReferencesNoVendorSdkPackages()
    {
        string[] deniedPrefixes =
        [
            "Anthropic",
            "OpenAI",
            "Gemini",
            "Amazon",
            "Azure.AI",
            "Microsoft.Agents",
            "Microsoft.SemanticKernel",
            "ModelContextProtocol",
            "OpenTelemetry",
            "Nacos",
            "A2A"
        ];

        // Microsoft.Extensions.AI.Abstractions is allowed (Core types reference IChatClient);
        // the implementation package is not.
        string[] deniedExactly = ["Microsoft.Extensions.AI"];

        var violations = RepositoryLayout.PackageReferences(RepositoryLayout.CoreProject)
            .Where(package =>
                deniedExactly.Contains(package, StringComparer.OrdinalIgnoreCase) ||
                deniedPrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "OpenClaw.Core must not reference a vendor SDK or provider implementation: " +
            string.Join(", ", violations));
    }

    /// <summary>Core is a library, not a web host; ASP.NET types belong to the Gateway.</summary>
    [Fact]
    public void Core_ReferencesNoAspNetCorePackages()
    {
        var violations = RepositoryLayout.PackageReferences(RepositoryLayout.CoreProject)
            .Where(package => package.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "OpenClaw.Core must not reference ASP.NET Core: " + string.Join(", ", violations));
    }

    /// <summary>The runtime owns no hosting, transport or endpoint concerns.</summary>
    [Fact]
    public void Agent_DoesNotReferenceGateway()
    {
        var references = RepositoryLayout.ProjectReferences(RepositoryLayout.Project("OpenClaw.Agent"));

        Assert.True(
            !references.Contains("OpenClaw.Gateway", StringComparer.Ordinal),
            "OpenClaw.Agent must not reference OpenClaw.Gateway.");
    }

    /// <summary>
    /// Only a composition root may know the Gateway: today that is the test host alone. A product
    /// project that references it has inverted the dependency direction and will pull the whole host
    /// (ASP.NET, every channel, every provider) into whatever it was supposed to stay small.
    /// </summary>
    [Fact]
    public void OnlyTestProjectsReferenceGateway()
    {
        var offenders = RepositoryLayout.AllProjects()
            .Where(project => RepositoryLayout.ProjectReferences(project.Path)
                .Contains("OpenClaw.Gateway", StringComparer.Ordinal))
            .Select(project => project.Name)
            .Where(name => !name.EndsWith(".Tests", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Only test projects may reference OpenClaw.Gateway, but these do: " + string.Join(", ", offenders));
    }
}

/// <summary>Locates the repository and reads project files without referencing any of them.</summary>
internal static class RepositoryLayout
{
    public static string CoreProject => Project("OpenClaw.Core");

    public static string Root { get; } = FindRoot();

    public static string Project(string name)
        => Path.Combine(Root, "src", name, $"{name}.csproj");

    public static IReadOnlyList<(string Name, string Path)> AllProjects()
        => Directory
            .EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path.GetFileNameWithoutExtension(path), path))
            .OrderBy(project => project.Item1, StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlyList<string> ProjectReferences(string projectPath)
        => ReadItems(projectPath, "ProjectReference", "Include")
            .Select(NormalizeProjectName)
            .ToArray();

    public static IReadOnlyList<string> PackageReferences(string projectPath)
        => ReadItems(projectPath, "PackageReference", "Include").ToArray();

    /// <summary>
    /// <c>..\OpenClaw.Gateway\OpenClaw.Gateway.csproj</c> and <c>../OpenClaw.Gateway/OpenClaw.Gateway.csproj</c>
    /// denote the same project; compare on the file name so the assertion does not depend on slash style.
    /// </summary>
    private static string NormalizeProjectName(string include)
        => Path.GetFileNameWithoutExtension(include.Replace('\\', '/').Trim());

    private static IEnumerable<string> ReadItems(string projectPath, string element, string attribute)
    {
        var document = XDocument.Load(projectPath);
        return document
            .Descendants()
            .Where(node => node.Name.LocalName == element)
            .Select(node => node.Attribute(attribute)?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenClaw.Net.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no OpenClaw.Net.slnx above " + AppContext.BaseDirectory + ").");
    }
}
