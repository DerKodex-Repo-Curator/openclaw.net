using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenClaw.Agent.Plugins;

/// <summary>
/// Discovers tools from configured MCP servers and registers them as native OpenClaw tools.
/// </summary>
public sealed class McpServerToolRegistry : IDisposable, IAsyncDisposable
{
    private readonly McpPluginsConfig _config;
    private readonly ILogger _logger;
    private readonly IMcpDelegatedToolInvoker? _delegatedToolInvoker;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly List<DiscoveredMcpTool> _tools = [];
    private readonly List<McpClient> _clients = [];
    private Task? _disposeTask;
    private bool _loaded;
    private bool _registered;
    private bool _disposed;

    private readonly Dictionary<string, (McpClient Client, List<DiscoveredMcpTool> Tools, McpServerConfig Config)> _workspaceServers
        = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpClient> _clientsByServerId = new(StringComparer.Ordinal);
    
    
    
    /// <summary>
    /// Creates a registry for configured MCP servers.
    /// </summary>
    public McpServerToolRegistry(
        McpPluginsConfig config,
        ILogger logger,
        IMcpDelegatedToolInvoker? delegatedToolInvoker = null)
    {
        _config = config;
        _logger = logger;
        _delegatedToolInvoker = delegatedToolInvoker;
    }

    /// <summary>
    /// Connects to configured MCP servers and registers discovered tools into the native registry.
    /// </summary>
    public async Task RegisterToolsAsync(NativePluginRegistry nativeRegistry, CancellationToken ct)
    {
        ThrowIfDisposed();
        await _loadSemaphore.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (_registered)
                return;

            var tools = await LoadInternalAsync(ct);
            foreach (var tool in tools)
                nativeRegistry.RegisterExternalTool(tool.Tool, tool.PluginId, tool.Detail);

            _registered = true;
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    internal async Task<IReadOnlyList<DiscoveredMcpTool>> LoadAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        await _loadSemaphore.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            return _loaded ? _tools : await LoadInternalAsync(ct);
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    public McpClient? GetClientByServerId(string serverId)
    {
        if (_clientsByServerId.TryGetValue(serverId, out var configured))
            return configured;

        return _workspaceServers.TryGetValue(serverId, out var workspace) ? workspace.Client : null;
    }

    /// <summary>
    /// Every server the health probe loop should watch: configured servers that are enabled plus
    /// workspace servers that are currently connected. A server with no client (it failed to
    /// connect at startup) is still listed with <see cref="McpProbeTarget.Client"/> = null so the
    /// monitor reports it as failing instead of silently forgetting it.
    /// </summary>
    public async Task<IReadOnlyList<McpProbeTarget>> GetProbeTargetsAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        await _loadSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            var targets = new List<McpProbeTarget>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (_config.Enabled && _config.Servers is { } configured)
            {
                foreach (var (serverId, serverConfig) in configured)
                {
                    if (!serverConfig.Enabled || !seen.Add(serverId))
                        continue;

                    _clientsByServerId.TryGetValue(serverId, out var client);
                    targets.Add(new McpProbeTarget(serverId, client, IsWorkspace: false));
                }
            }

            foreach (var (serverId, state) in _workspaceServers)
            {
                if (seen.Add(serverId))
                    targets.Add(new McpProbeTarget(serverId, state.Client, IsWorkspace: true));
            }

            return targets.OrderBy(static t => t.ServerId, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    /// <summary>
    /// Re-lists one server's tools and reconciles the registry's tool table with the result.
    /// </summary>
    /// <remarks>
    /// Used when a probe sees a tool inventory that differs from the one already registered
    /// (T-B5-5): the caller applies the returned delta to the runtime so the model is offered the
    /// tools that actually exist.
    /// </remarks>
    public async Task<McpWorkspaceReloadResult> RefreshServerToolsAsync(string serverId, CancellationToken ct)
    {
        ThrowIfDisposed();
        await _loadSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            McpClient client;
            McpServerConfig serverConfig;
            var isWorkspace = false;

            if (_workspaceServers.TryGetValue(serverId, out var workspace))
            {
                client = workspace.Client;
                serverConfig = workspace.Config;
                isWorkspace = true;
            }
            else if (_clientsByServerId.TryGetValue(serverId, out var configuredClient) &&
                     _config.Servers is { } servers &&
                     servers.TryGetValue(serverId, out var configuredConfig))
            {
                client = configuredClient;
                serverConfig = configuredConfig;
            }
            else
            {
                return new McpWorkspaceReloadResult([], []);
            }

            var displayName = string.IsNullOrWhiteSpace(serverConfig.Name) ? serverId : serverConfig.Name!;
            var pluginId = $"mcp:{serverId}";
            var descriptors = await LoadToolsFromClientAsync(client, serverId, pluginId, displayName, serverConfig, ct)
                .ConfigureAwait(false);
            var refreshed = descriptors
                .Select(descriptor => CreateDiscoveredTool(
                    client, descriptor, serverConfig, serverId, displayName, pluginId, useDelegation: !isWorkspace))
                .ToList();

            var existing = _tools.Where(tool => string.Equals(tool.PluginId, pluginId, StringComparison.Ordinal)).ToList();
            var refreshedNames = refreshed.Select(tool => tool.Tool.Name).ToHashSet(StringComparer.Ordinal);
            var existingNames = existing.Select(tool => tool.Tool.Name).ToHashSet(StringComparer.Ordinal);

            var removed = new List<string>();
            foreach (var stale in existing)
            {
                if (refreshedNames.Contains(stale.Tool.Name))
                    continue;

                _tools.Remove(stale);
                removed.Add(stale.Tool.Name);
            }

            var added = refreshed.Where(tool => !existingNames.Contains(tool.Tool.Name)).ToList();
            _tools.AddRange(added);

            if (isWorkspace)
                _workspaceServers[serverId] = (client, refreshed, serverConfig);

            return new McpWorkspaceReloadResult([.. added.Select(tool => tool.Tool)], removed);
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    public async Task<McpWorkspaceReloadResult> ReloadWorkspaceServersAsync(
        Dictionary<string, McpServerConfig>? newServers,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        await _loadSemaphore.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();

            var addedTools = new List<ITool>();
            var removedToolNames = new List<string>();
            var desiredServers = newServers is null
                ? new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                : new Dictionary<string, McpServerConfig>(newServers, StringComparer.Ordinal);

            var serversToRemove = new List<string>();
            foreach (var (serverId, serverState) in _workspaceServers)
            {
                if (!desiredServers.TryGetValue(serverId, out var newConfig) ||
                    !newConfig.Enabled ||
                    !ServerConfigEquivalent(serverState.Config, newConfig))
                {
                    serversToRemove.Add(serverId);
                }
            }

            foreach (var serverId in serversToRemove)
            {
                var workspaceState = _workspaceServers[serverId];
                _workspaceServers.Remove(serverId);
                _clients.Remove(workspaceState.Client);

                foreach (var tool in workspaceState.Tools)
                {
                    removedToolNames.Add(tool.Tool.Name);
                    _tools.Remove(tool);
                }

                await DisposeClientAsync(workspaceState.Client).ConfigureAwait(false);
            }

            foreach (var (serverId, serverConfig) in desiredServers)
            {
                if (!serverConfig.Enabled || _workspaceServers.ContainsKey(serverId))
                    continue;

                McpClient? client = null;
                try
                {
                    var transport = CreateTransport(serverId, serverConfig);
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(serverConfig.StartupTimeoutSeconds));
                    client = await McpClient.CreateAsync(transport, cancellationToken: timeoutCts.Token);

                    var displayName = string.IsNullOrWhiteSpace(serverConfig.Name) ? serverId : serverConfig.Name!;
                    var pluginId = $"mcp:{serverId}";
                    var descriptors = await LoadToolsFromClientAsync(client, serverId, pluginId, displayName, serverConfig, ct);
                    var discoveredTools = descriptors
                        .Select(tool => CreateDiscoveredTool(
                            client, tool, serverConfig, serverId, displayName, pluginId, useDelegation: false))
                        .ToList();

                    _workspaceServers[serverId] = (client, discoveredTools, CloneServerConfig(serverConfig));
                    _clients.Add(client);
                    _tools.AddRange(discoveredTools);
                    addedTools.AddRange(discoveredTools.Select(tool => tool.Tool));
                }
                catch (Exception ex)
                {
                    if (client is not null)
                    {
                        try
                        {
                            await DisposeClientAsync(client).ConfigureAwait(false);
                        }
                        catch (Exception disposeEx)
                        {
                            _logger.LogDebug(disposeEx, "Workspace MCP: failed to dispose client for server '{ServerId}' after connection failure.", serverId);
                        }
                    }

                    _logger.LogError(ex, "Workspace MCP: failed to connect to server '{ServerId}', skipping", serverId);
                }
            }

            return new McpWorkspaceReloadResult(addedTools, removedToolNames);
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    private async Task<IReadOnlyList<DiscoveredMcpTool>> LoadInternalAsync(CancellationToken ct)
    {
        if (_loaded)
            return _tools;

        if (!_config.Enabled)
        {
            _loaded = true;
            return _tools;
        }

        var discoveredTools = new List<DiscoveredMcpTool>();
        var discoveredClients = new List<McpClient>();

        try
        {
            foreach (var (serverId, serverConfig) in _config.Servers ?? [])
            {
                if (!serverConfig.Enabled)
                    continue;

                var transport = CreateTransport(serverId, serverConfig);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(serverConfig.StartupTimeoutSeconds));
                var client = await McpClient.CreateAsync(transport, cancellationToken: timeoutCts.Token);
                discoveredClients.Add(client);

                var displayName = string.IsNullOrWhiteSpace(serverConfig.Name) ? serverId : serverConfig.Name!;
                var pluginId = $"mcp:{serverId}";

                var tools = await LoadToolsFromClientAsync(client, serverId, pluginId, displayName, serverConfig, ct);
                _clientsByServerId[serverId] = client;

                foreach (var tool in tools)
                    discoveredTools.Add(CreateDiscoveredTool(
                        client, tool, serverConfig, serverId, displayName, pluginId, useDelegation: true));
            }

            _clients.AddRange(discoveredClients);
            _tools.AddRange(discoveredTools);
            _loaded = true;
            return _tools;
        }
        catch
        {
            foreach (var client in discoveredClients)
            {
                try
                {
                    await DisposeClientAsync(client);
                }
                catch
                {
                }
            }
            throw;
        }
    }

    private async Task<IReadOnlyList<McpToolDescriptor>> LoadToolsFromClientAsync(
        McpClient client,
        string serverId,
        string pluginId,
        string displayName,
        McpServerConfig config,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.RequestTimeoutSeconds));

        var allTools = new List<Tool>();
        var listParams = new ListToolsRequestParams();
        do
        {
            var page = await client.ListToolsAsync(listParams, cancellationToken: timeoutCts.Token);
            allTools.AddRange(page.Tools);
            var next = page.NextCursor;
            listParams = new ListToolsRequestParams { Cursor = string.IsNullOrEmpty(next) ? null : next };
        }
        while (listParams.Cursor is not null);
        var tools = new List<McpToolDescriptor>();

        foreach (var tool in allTools)
        {
            var remoteName = tool.Name;
            if (string.IsNullOrWhiteSpace(remoteName))
                throw new InvalidOperationException($"MCP server '{displayName}' returned a tool entry with an empty name.");

            var meta = tool.Meta;
            if (!IsToolModelVisible(meta))
            {
                _logger.LogInformation(
                    "MCP server '{DisplayName}': skipping app-only tool '{Tool}' (visibility excludes model).",
                    displayName,
                    remoteName);
                continue;
            }

            var localName = ResolveToolName(serverId, config.ToolNamePrefix, remoteName);
            var description = !string.IsNullOrWhiteSpace(tool.Description)
                ? $"{tool.Description} (from MCP server '{displayName}')"
                : $"MCP tool '{remoteName}' from server '{displayName}'.";
            var inputSchema = ResolveInputSchemaText(tool.InputSchema);
            var hasUi = ToolHasUi(meta);
            tools.Add(new McpToolDescriptor(localName, remoteName, description, inputSchema, hasUi));
        }

        _logger.LogInformation("MCP server enabled: {ServerId} ({DisplayName}) with {ToolCount} tool(s)",
            serverId, displayName, tools.Count);
        return tools;
    }

    /// <summary>
    /// Builds one native tool for a discovered descriptor.
    /// </summary>
    /// <param name="useDelegation">
    /// Configured servers keep their delegated-credential and per-request timeout settings. Workspace
    /// servers historically ignored both, so they keep that shape instead of silently changing how
    /// they authenticate.
    /// </param>
    private DiscoveredMcpTool CreateDiscoveredTool(
        McpClient client,
        McpToolDescriptor descriptor,
        McpServerConfig config,
        string serverId,
        string displayName,
        string pluginId,
        bool useDelegation)
    {
        var tool = useDelegation
            ? new McpNativeTool(
                client,
                descriptor.LocalName,
                descriptor.RemoteName,
                descriptor.Description,
                descriptor.InputSchemaText,
                descriptor.HasUi,
                _delegatedToolInvoker,
                config.DelegatedCredentials,
                serverId,
                string.Equals(config.NormalizeTransport(), "http", StringComparison.Ordinal) && config.Url is not null
                    ? new Uri(config.Url)
                    : null,
                ResolveHeaders(config.Headers),
                config.RequestTimeoutSeconds,
                serverId)
            : new McpNativeTool(
                client,
                descriptor.LocalName,
                descriptor.RemoteName,
                descriptor.Description,
                descriptor.InputSchemaText,
                descriptor.HasUi,
                serverId: serverId);

        return new DiscoveredMcpTool(pluginId, tool, displayName);
    }

    private static string ResolveToolName(string serverId, string? toolNamePrefix, string remoteName)
    {
        var prefix = toolNamePrefix;
        if (prefix is null)
            prefix = $"{SanitizePrefixPart(serverId)}.";

        var sanitizedRemoteName = SanitizeLlmToolNamePart(remoteName);
        // Dots are not allowed by OpenAI-compatible APIs (^[a-zA-Z0-9_-]+$).
        // Replace any dot in the final assembled name with underscore.
        var name = string.IsNullOrEmpty(prefix) ? sanitizedRemoteName : prefix + sanitizedRemoteName;
        return name.Replace('.', '_');
    }

    private static string SanitizePrefixPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "mcp";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (IsLlmToolNameChar(ch))
                sb.Append(char.ToLowerInvariant(ch));
            else if (ch > 0x7F)
                sb.Append($"_u{(int)ch:x4}");
            else
                sb.Append('_');
        }

        return sb.Length == 0 ? "mcp" : sb.ToString();
    }

    /// <summary>
    /// Sanitizes a string so every character satisfies the LLM tool-name pattern <c>^[a-zA-Z0-9_-]+$</c>.
    /// Dots are replaced with <c>_</c>; other non-conforming ASCII characters are also replaced with <c>_</c>;
    /// non-ASCII characters are replaced with <c>_uXXXX</c> (lowercase hex code point).
    /// </summary>
    private static string SanitizeLlmToolNamePart(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (IsLlmToolNameChar(ch))
                sb.Append(ch);
            else if (ch > 0x7F)
                sb.Append($"_u{(int)ch:x4}");
            else
                sb.Append('_');
        }

        return sb.Length == 0 ? "_" : sb.ToString();
    }

    private static bool IsLlmToolNameChar(char ch)
        => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
           || ch is '_' or '-';

    private static string ResolveInputSchemaText(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new JsonException("MCP tool payload is missing required inputSchema.");

        return inputSchema.GetRawText();
    }

    internal static bool IsToolModelVisible(JsonObject? meta)
    {
        if (meta is null)
            return true;
        if (meta["ui"] is not JsonObject ui)
            return true;
        if (ui["visibility"] is not JsonArray visibility)
            return true;

        return visibility
            .OfType<JsonValue>()
            .Any(static value =>
                value.TryGetValue<string>(out var role) &&
                string.Equals(role, "model", StringComparison.Ordinal));
    }

    internal static bool ToolHasUi(JsonObject? meta)
    {
        if (meta is null)
            return false;

        if (meta["ui"] is JsonObject ui &&
            ui["resourceUri"] is JsonValue resourceValue &&
            resourceValue.TryGetValue<string>(out var resourceUri) &&
            !string.IsNullOrEmpty(resourceUri))
        {
            return true;
        }

        if (meta["ui/resourceUri"] is JsonValue flatValue &&
            flatValue.TryGetValue<string>(out var flatResourceUri) &&
            !string.IsNullOrEmpty(flatResourceUri))
        {
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            disposeTask = _disposeTask;
        }

        await disposeTask.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static IClientTransport CreateTransport(string serverId, McpServerConfig config)
    {
        var transport = config.NormalizeTransport();
        return transport switch
        {
            "stdio" => new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = config.Command!,
                Arguments = config.Arguments ?? [],
                WorkingDirectory = config.WorkingDirectory,
                EnvironmentVariables = ResolveEnv(config.Environment),
                Name = serverId,
            }),
            "http" => CreateHttpTransport(serverId, config, HttpTransportMode.StreamableHttp),
            "sse"  => CreateHttpTransport(serverId, config, HttpTransportMode.Sse),
            _ => throw new InvalidOperationException($"Unsupported MCP transport '{config.Transport}' for server '{serverId}'.")
        };
    }

    private static HttpClientTransport CreateHttpTransport(string serverId, McpServerConfig config, HttpTransportMode mode)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(config.Url!),
            AdditionalHeaders = ResolveHeaders(config.Headers),
            TransportMode = mode,
            Name = serverId,
        };
        var httpClient = new HttpClient(new RemoveCharsetDelegatingHandler());
        return new HttpClientTransport(options, httpClient, ownsHttpClient: true);
    }

    private static Dictionary<string, string?>? ResolveEnv(Dictionary<string, string>? environment)
    {
        if (environment is null || environment.Count == 0)
            return null;

        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, rawValue) in environment)
        {
            if (rawValue is null)
            {
                resolved[name] = null;
                continue;
            }
            var value = SecretResolver.Resolve(rawValue);
            if (value is null && rawValue.StartsWith("env:", StringComparison.Ordinal))
                throw new InvalidOperationException($"Environment variable '{name}' references unset env var '{rawValue[4..]}'");
            resolved[name] = value ?? rawValue;
        }

        return resolved;
    }

    private static Dictionary<string, string>? ResolveHeaders(Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return null;

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, rawValue) in headers)
        {
            if (rawValue is null)
            {
                resolved[name] = string.Empty;
                continue;
            }
            var value = SecretResolver.Resolve(rawValue);
            if (value is null && rawValue.StartsWith("env:", StringComparison.Ordinal))
                throw new InvalidOperationException($"Header '{name}' references unset env var '{rawValue[4..]}'");
            resolved[name] = value ?? rawValue;
        }

        return resolved;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(McpServerToolRegistry));
    }

    private async Task DisposeCoreAsync()
    {
        List<McpClient> clients;

        await _loadSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            clients = [.. _clients];
            _clients.Clear();
            _tools.Clear();
            _workspaceServers.Clear();
            _clientsByServerId.Clear();
        }
        finally
        {
            _loadSemaphore.Release();
        }

        foreach (var client in clients)
        {
            try
            {
                await DisposeClientAsync(client).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static async ValueTask DisposeClientAsync(McpClient client)
    {
        if (client is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (client is IDisposable disposable)
            disposable.Dispose();
    }

    private sealed class RemoveCharsetDelegatingHandler : DelegatingHandler
    {
        private readonly HttpClientHandler _httpClientHandler;

        public RemoveCharsetDelegatingHandler()
            : base(CreateHttpClientHandler())
        {
            _httpClientHandler = (HttpClientHandler)InnerHandler!;
        }

        private static HttpClientHandler CreateHttpClientHandler()
            => new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content?.Headers?.ContentType is { CharSet: not null } contentType)
                contentType.CharSet = null;
            return base.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpClientHandler.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed record DiscoveredMcpTool(string PluginId, ITool Tool, string Detail);

    private static McpServerConfig CloneServerConfig(McpServerConfig config)
        => new()
        {
            Enabled = config.Enabled,
            Name = config.Name,
            Transport = config.Transport,
            Command = config.Command,
            Arguments = [.. config.Arguments],
            WorkingDirectory = config.WorkingDirectory,
            Url = config.Url,
            ToolNamePrefix = config.ToolNamePrefix,
            StartupTimeoutSeconds = config.StartupTimeoutSeconds,
            RequestTimeoutSeconds = config.RequestTimeoutSeconds,
            Environment = new Dictionary<string, string>(config.Environment, StringComparer.Ordinal),
            Headers = new Dictionary<string, string>(config.Headers, StringComparer.Ordinal),
        };

    private static bool ServerConfigEquivalent(McpServerConfig left, McpServerConfig right)
    {
        return left.Enabled == right.Enabled &&
            string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
            string.Equals(left.NormalizeTransport(), right.NormalizeTransport(), StringComparison.Ordinal) &&
            string.Equals(left.Command, right.Command, StringComparison.Ordinal) &&
            SequenceEqual(left.Arguments, right.Arguments) &&
            string.Equals(left.WorkingDirectory, right.WorkingDirectory, StringComparison.Ordinal) &&
            string.Equals(left.Url, right.Url, StringComparison.Ordinal) &&
            string.Equals(left.ToolNamePrefix, right.ToolNamePrefix, StringComparison.Ordinal) &&
            left.StartupTimeoutSeconds == right.StartupTimeoutSeconds &&
            left.RequestTimeoutSeconds == right.RequestTimeoutSeconds &&
            DictionaryEqual(left.Environment, right.Environment) &&
            DictionaryEqual(left.Headers, right.Headers);
    }

    private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.Count != right.Count)
            return false;

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var rightValue) ||
                !string.Equals(value, rightValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
    private sealed record McpToolDescriptor(string LocalName, string RemoteName, string Description, string InputSchemaText, bool HasUi);
}

/// <summary>One server the health probe loop should watch.</summary>
/// <param name="ServerId">Configured or workspace server id.</param>
/// <param name="Client">Live client; <see langword="null"/> when the server never connected, which
/// the monitor reports as a transport failure rather than dropping the server from the fleet.</param>
/// <param name="IsWorkspace">Whether the server came from the workspace <c>mcp.json</c> rather than
/// gateway configuration.</param>
public sealed record McpProbeTarget(string ServerId, McpClient? Client, bool IsWorkspace);

/// <summary>Tools a reconcile added and removed for one server.</summary>
public sealed record McpWorkspaceReloadResult(
    IReadOnlyList<ITool> AddedTools,
    IReadOnlyList<string> RemovedToolNames);
