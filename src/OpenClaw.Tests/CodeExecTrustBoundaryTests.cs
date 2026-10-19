using OpenClaw.Agent.Tools;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Pins the trust boundary <c>docs/security/code-exec-approval-boundary.md</c> describes: the Docker
/// backend is network-isolated, and the process backend is not a sandbox at all.
/// </summary>
public sealed class CodeExecTrustBoundaryTests
{
    [Fact]
    public void DockerBackend_IsNotGatewayPrivileged()
    {
        var config = new CodeExecConfig { Enabled = true, Backend = "docker" };

        Assert.False(CodeExecTool.RunsWithGatewayPrivileges(config));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("Process")]
    [InlineData("something-else")]
    public void AnythingButDocker_RunsWithGatewayPrivileges(string backend)
    {
        // Fail closed on an unknown backend name: only the isolated one counts as isolated.
        var config = new CodeExecConfig { Enabled = true, Backend = backend };

        Assert.True(CodeExecTool.RunsWithGatewayPrivileges(config));
    }

    [Fact]
    public void DisabledTool_HasNoPrivilege()
    {
        var config = new CodeExecConfig { Enabled = false, Backend = "process" };

        Assert.False(CodeExecTool.RunsWithGatewayPrivileges(config));
    }

    [Fact]
    public void DockerArguments_DisableNetworkingAndMountCodeReadOnly()
    {
        var config = new CodeExecConfig { Enabled = true, Backend = "docker", DockerImage = "python:3.12-slim" };

        var args = CodeExecTool.BuildDockerArguments(config, "/tmp/code.py", ".py", "python3", string.Empty);
        var joined = string.Join(' ', args);

        // --network none is what makes the audit conclusion hold: generated code cannot reach the
        // gateway's own HTTP/MCP surface. Removing it must break this test, not silently widen scope.
        Assert.Contains("--network", args, StringComparer.Ordinal);
        var networkIndex = args.IndexOf("--network");
        Assert.Equal("none", args[networkIndex + 1]);

        Assert.Contains("/tmp/code.py:/code.py:ro", joined, StringComparison.Ordinal);
        Assert.Contains("python:3.12-slim", joined, StringComparison.Ordinal);
    }
}
