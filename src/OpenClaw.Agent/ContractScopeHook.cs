using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Agent.Tools;

namespace OpenClaw.Agent;

/// <summary>
/// Enforces contract-scoped tool restrictions at tool-call time.
/// Checks path-scoped capabilities and tool call count limits.
/// </summary>
/// <remarks>
/// Implements <see cref="IToolHookDenialReason"/> so a refusal reaches the model as an actionable
/// message rather than "denied by hook: ContractScope". The explanation is derived by re-running the
/// same <see cref="Evaluate"/> the decision used, so the two cannot drift apart.
/// </remarks>
public sealed class ContractScopeHook : IToolHookWithContext, IToolHookDenialReason
{
    private readonly Func<string, ContractPolicy?> _contractResolver;
    private readonly Func<string, int> _toolCallCounter;
    private readonly ILogger _logger;

    public string Name => "ContractScope";

    /// <param name="contractResolver">Resolves a session ID to its contract policy (or null).</param>
    /// <param name="toolCallCounter">
    /// Returns the tool calls already made in the session's <em>current turn</em>. Counting across the
    /// whole session would make a long-lived conversation permanently unable to call tools.
    /// </param>
    /// <param name="logger">Logger instance.</param>
    public ContractScopeHook(
        Func<string, ContractPolicy?> contractResolver,
        Func<string, int> toolCallCounter,
        ILogger logger)
    {
        _contractResolver = contractResolver;
        _toolCallCounter = toolCallCounter;
        _logger = logger;
    }

    public ValueTask<bool> BeforeExecuteAsync(string toolName, string arguments, CancellationToken ct)
        => ValueTask.FromResult(true); // No-op for non-context path; context variant handles enforcement.

    public ValueTask<bool> BeforeExecuteAsync(ToolHookContext context, CancellationToken ct)
    {
        var policy = _contractResolver(context.SessionId);
        if (policy is null)
            return ValueTask.FromResult(true);

        return ValueTask.FromResult(Evaluate(policy, context, log: true) is null);
    }

    /// <inheritdoc />
    public ToolHookDenial? ExplainDenial(ToolHookContext context)
    {
        var policy = _contractResolver(context.SessionId);
        return policy is null ? null : Evaluate(policy, context, log: false);
    }

    /// <summary>
    /// Returns <see langword="null"/> when the call is allowed, otherwise the refusal to report.
    /// </summary>
    /// <param name="log">
    /// Whether to emit the operator-facing log line. Explanations re-run this without logging so a
    /// single denial is not logged twice.
    /// </param>
    private ToolHookDenial? Evaluate(ContractPolicy policy, ToolHookContext context, bool log)
    {
        if (policy.MaxToolCalls > 0)
        {
            var count = _toolCallCounter(context.SessionId);
            if (count >= policy.MaxToolCalls)
            {
                if (log)
                {
                    _logger.LogInformation(
                        "ContractScope: denied tool {Tool} for session {Session} — MaxToolCalls ({Max}) reached",
                        context.ToolName, context.SessionId, policy.MaxToolCalls);
                }

                return new ToolHookDenial(
                    $"Tool call budget exhausted for this turn: this session's contract allows " +
                    $"{policy.MaxToolCalls} tool calls and {count} have already been made. Do not call " +
                    $"more tools; answer from the results you already have.",
                    ToolFailureCodes.ToolCallBudgetExhausted,
                    "Raise the contract's MaxToolCalls, or narrow the request so it needs fewer tool calls.");
            }
        }

        var scope = FindScope(policy, context.ToolName);
        var hasScopedFilesystemCapability = HasScopedFilesystemCapability(policy);
        if (scope is null && hasScopedFilesystemCapability && IsFilesystemAffectingTool(context.ToolName))
        {
            // Shell is always denied under scoped contracts unless explicitly granted
            // with an unscoped shell capability (AllowedPaths empty). Other filesystem
            // tools are denied because they lack a matching scope entry.
            if (context.ToolName is "shell" or "code_exec")
            {
                if (log)
                {
                    _logger.LogInformation(
                        "ContractScope: denied tool {Tool} for session {Session} — shell/exec tools require an explicit grant under scoped filesystem contracts",
                        context.ToolName,
                        context.SessionId);
                }

                return new ToolHookDenial(
                    "This session's contract only grants scoped filesystem access, so shell and code " +
                    "execution tools are unavailable. Use the file tools that were granted, or ask the " +
                    "operator to grant shell access.");
            }

            if (log)
            {
                _logger.LogInformation(
                    "ContractScope: denied tool {Tool} for session {Session} — tool is unscoped under a scoped filesystem contract",
                    context.ToolName,
                    context.SessionId);
            }

            return new ToolHookDenial(
                $"The {context.ToolName} tool is not granted by this session's contract. Only the file " +
                "tools covered by a scoped capability can be used.");
        }

        if (scope is not null && scope.AllowedPaths.Length > 0)
        {
            if (TryExtractScopedPaths(context.ToolName, context.ArgumentsJson, out var paths))
            {
                foreach (var path in paths)
                {
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    if (!IsPathAllowed(path, scope.AllowedPaths))
                    {
                        if (log)
                        {
                            _logger.LogInformation(
                                "ContractScope: denied tool {Tool} path {Path} for session {Session} — outside scoped paths",
                                context.ToolName, path, context.SessionId);
                        }

                        return new ToolHookDenial(
                            $"The path given to {context.ToolName} is outside the folders this session's " +
                            "contract grants. Retry inside a granted folder, or ask the operator to widen " +
                            "the contract.");
                    }
                }
            }
            else if (RequiresResolvedScopedPath(context.ToolName))
            {
                if (log)
                {
                    _logger.LogInformation(
                        "ContractScope: denied tool {Tool} for session {Session} — scoped path could not be resolved safely",
                        context.ToolName,
                        context.SessionId);
                }

                return new ToolHookDenial(
                    $"The {context.ToolName} call could not be checked against this session's contract " +
                    "because its target path could not be resolved. Supply an explicit path inside a " +
                    "granted folder.");
            }
        }

        return null;
    }

    public ValueTask AfterExecuteAsync(string toolName, string arguments, string result, TimeSpan duration, bool failed, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask AfterExecuteAsync(ToolHookContext context, string result, TimeSpan duration, bool failed, CancellationToken ct)
        => ValueTask.CompletedTask;

    private static ScopedCapability? FindScope(ContractPolicy policy, string toolName)
    {
        foreach (var scope in policy.ScopedCapabilities)
        {
            if (string.Equals(scope.ToolName, toolName, StringComparison.Ordinal))
                return scope;
        }
        return null;
    }

    private static bool HasScopedFilesystemCapability(ContractPolicy policy)
        => policy.ScopedCapabilities.Any(scope => scope.AllowedPaths.Length > 0);

    private static bool IsFilesystemAffectingTool(string toolName)
        => toolName is "shell" or "code_exec" or "git" or "process" or "file_read" or "file_write" or "edit_file" or "apply_patch";

    private static bool IsPathAllowed(string path, string[] allowedPaths)
    {
        var expanded = ExpandTilde(path);
        var full = ToolPathPolicy.ResolveRealPath(expanded);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var allowed in allowedPaths)
        {
            var allowedExpanded = ExpandTilde(allowed.Trim());
            var allowedFull = ToolPathPolicy.ResolveRealPath(allowedExpanded);

            if (string.Equals(full, allowedFull, comparison))
                return true;

            var root = allowedFull.EndsWith(Path.DirectorySeparatorChar)
                ? allowedFull
                : allowedFull + Path.DirectorySeparatorChar;

            if (full.StartsWith(root, comparison))
                return true;
        }

        return false;
    }

    private static bool TryExtractScopedPaths(string toolName, string arguments, out IReadOnlyList<string> paths)
    {
        paths = [];

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var root = doc.RootElement;
            var extracted = toolName switch
            {
                "git" => TryReadStringList(root, "cwd", out paths),
                "process" => TryReadProcessPaths(root, out paths),
                "shell" => false,
                "file_read" or "file_write" or "edit_file" or "apply_patch" => TryReadStringList(root, "path", out paths),
                _ => TryReadStringList(root, "path", out paths)
            };

            return extracted && paths.Count > 0;
        }
        catch { }

        return false;
    }

    private static bool RequiresResolvedScopedPath(string toolName)
        => toolName is "git" or "process" or "shell";

    private static bool TryReadProcessPaths(JsonElement root, out IReadOnlyList<string> paths)
    {
        paths = [];
        var action = root.TryGetProperty("action", out var actionProp) && actionProp.ValueKind == JsonValueKind.String
            ? actionProp.GetString()
            : null;
        if (!string.Equals(action, "start", StringComparison.OrdinalIgnoreCase))
            return false;

        return TryReadStringList(root, "working_directory", out paths);
    }

    private static bool TryReadStringList(JsonElement root, string propertyName, out IReadOnlyList<string> paths)
    {
        paths = [];
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return false;

        var path = value.GetString();
        if (string.IsNullOrWhiteSpace(path))
            return false;

        paths = [path];
        return true;
    }

    private static string ExpandTilde(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }
        return path;
    }
}
