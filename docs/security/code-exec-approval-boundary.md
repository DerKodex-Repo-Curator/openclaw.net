# 生成代码能否绕过审批门禁——审计结论

> 范围：`code_exec` / `shell` 两个工具执行**模型生成的代码**时，是否能触达 `ApprovalRequiredTools` 里那些"必须人工批准"的工具。
> 结论日期：2026-10-18。相关代码：`src/OpenClaw.Agent/Tools/CodeExecTool.cs`、`src/OpenClaw.Agent/Tools/ShellTool.cs`、`src/OpenClaw.Agent/Plugins/NativePluginRegistry.cs`。

## 结论（一句话）

**可以，但只在 `code_exec.Backend=process`（默认值）时成立。** 审批门禁是**聊天/工具循环路径上的控制**，不是进程边界上的控制；`process` 后端下生成的代码以网关自身权限运行，因此它能走到网关自己的 HTTP/MCP 入口，而那条路径按**调用者令牌**鉴权，不会再次询问"这次调用是否已获人工批准"。

`docker` 后端不受影响：它以 `--network none` 启动（`CodeExecTool.BuildDockerArguments`），生成代码没有可达的网络。

## 证据

| 事实 | 位置 |
| --- | --- |
| `code_exec` 默认后端是 `process` | `src/OpenClaw.Core/Plugins/PluginModels.cs:528` `Backend = "process"` |
| `process` 后端不设置任何 `EnvironmentVariables`，也不限制网络，直接 `Process.Start` 子进程 | `CodeExecTool.RunProcessAsync`（`ProcessStartInfo` 仅重定向 stdout/stderr） |
| `shell` 同样以 `cmd.exe /c` 或 `/bin/sh -c` 启动子进程，继承环境与网络 | `ShellTool.ExecuteAsync` |
| `docker` 后端带 `--network none`，代码只读挂载 | `CodeExecTool.BuildDockerArguments` |
| MCP 端点的变更型工具**按调用者角色**校验，不经过聊天路径的工具审批回调 | `src/OpenClaw.Gateway/Mcp/McpServiceExtensions.cs:31` 注释 "Mutating MCP tools check the caller's role" |

因此攻击面不是"模型直接调用被门禁的工具"（那条路径会弹审批），而是"模型写一段代码 → 代码自己发一个带令牌的请求 → 网关认为这是一个合法外部调用者"。

令牌可达性决定了这条路径的实际风险：`process` 后端继承网关进程的完整环境变量，且生成代码通常与网关同机、可读同一份配置文件。这两个条件任意一个成立，路径就成立。

## 这不是一个可以就地修补的缺陷

在 `CodeExecTool` 里删环境变量、拦 `127.0.0.1` 都只是提高门槛，不是建立边界：进程内代码可以读磁盘上的配置文件、可以走非 loopback 地址、可以用 `--network` 之外的系统调用。**任意代码执行一旦与网关同权限，"审批"这一层语义就不成立了**——这属于信任模型，不属于实现 bug。

所以本次改动做的是**让这件事在启动时就可见**，而不是假装把它关掉：

1. `NativePluginRegistry` 在 `code_exec` 启用、后端非 `docker`、且配置了 `ApprovalRequiredTools` 时输出一条 **Warning**，明确指出"这些审批工具可从生成代码调用、且不会弹审批"，并给出改成 `docker` 的具体配置项。
2. `CodeExecTool.RunsWithGatewayPrivileges()` 把该判断固化为可测的 API（未知后端名一律视为"非隔离"，fail closed）。
3. `CodeExecTrustBoundaryTests` 把 `--network none` 与只读挂载钉住：将来有人删掉隔离参数，测试会失败而不是静默放开范围。
4. `docs/security/code-exec-approval-boundary.md`（本文件）作为该信任边界的正式说明。

## 运维指引

- **依赖审批策略做隔离时**，必须 `OpenClaw:Plugins:Native:CodeExec:Backend=docker`；`process` 只适用于"这台机器上跑的东西本来就完全可信"的场景（本地单人开发）。
- `shell` 工具没有沙箱后端，它本身就是高权限工具：应放进 `ApprovalRequiredTools`，并且只在受信会话中开启。
- 若必须使用 `process` 后端，请把网关的 operator 令牌从网关进程环境中移除（改用文件/外部 secret provider，并限制该文件对运行用户的读取权限），以切断"代码 → 令牌 → HTTP 入口"这条链。

## 未覆盖的范围

- `IToolSandbox`（OpenSandbox）路径：它按工具声明路由 `shell`/`code_exec`/`browser`，隔离强度由外部沙箱实现决定，本审计未评估。
- 插件桥（TS/JS）执行的代码：走的是同一进程权限模型，结论相同，但入口不同，未逐条展开。
