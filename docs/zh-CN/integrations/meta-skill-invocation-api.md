# MetaSkill 调用 API

`POST /api/integration/meta-invocations` 会在现有会话中直接执行明确指定的 MetaSkill DAG。它不会提交聊天消息，也不会启动外部 workflow。

## 身份验证

使用 Gateway 标准 operator 身份验证，并确保调用方具有 `integration.mutate` 权限。Bearer operator token 通过 `Authorization` header 传入。使用浏览器会话时，还必须发送登录时返回的 `X-CSRF-Token` header。endpoint 会先执行现有的 operator 授权和速率限制检查，然后才读取幂等键或请求 body。

调用方必须有权写入请求中的现有会话。系统会在查找或声明幂等键之前检查会话所有权，因此被拒绝的请求不会占用该键。

## 请求

发送唯一的 `Idempotency-Key` header（最长 200 个字符），并在 JSON body 中提供以下三个字段：

```http
POST /api/integration/meta-invocations HTTP/1.1
Authorization: Bearer <operator-token>
Idempotency-Key: incident-2026-10-02-run-1
Content-Type: application/json

{
  "skill": "incident-review",
  "input": "Review incident INC-123 and identify the next action.",
  "sessionId": "incident-123"
}
```

`skill` 和 `sessionId` 不能为空。`input` 可以是字符串或 `null`。幂等键按已认证的调用方隔离，不同调用方可以独立使用相同的键。使用相同的 `skill`、`input` 和 `sessionId` 重试同一个键，会返回原调用状态或已完成结果；同一个键对应不同请求时会返回冲突。

## 响应

| HTTP 状态 | 含义 |
| --- | --- |
| `200 OK` | 调用已完成，或运行正暂停等待人工处理（`AwaitingInput`，见下文）。重放请求会返回持久化的结果。 |
| `202 Accepted` | 相同请求仍在执行中。响应包含原始 `invocationId` 和 `Running` 状态；使用相同的键和请求重试即可查询状态。 |
| `409 Conflict` | 幂等键已用于不同请求，或调用状态为 `Uncertain`。键冲突返回错误对象；不确定调用会返回其 `invocationId`、`Uncertain` 状态和错误信息。 |

已完成响应示例：

```json
{
  "invocationId": "3a1d3589-2856-4f6b-b572-9f9e060db134",
  "status": "Completed",
  "result": "The next action is to rotate the affected credential.",
  "createdAtUtc": "2026-10-02T12:34:56Z"
}
```

JSON 无效或缺少必填字段时返回 `400 Bad Request`；会话不存在时返回 `404 Not Found`；调用方无权访问会话时返回 `403 Forbidden`。

## 暂停运行

运行可能在执行途中停下等待人工处理：等待自由输入（`user_input` 步骤）或等待工具审批（`tool_approval`）。此时调用状态为 `AwaitingInput`，HTTP 仍返回 `200 OK`，并附带 `pause` 描述：

```json
{
  "invocationId": "3a1d3589-2856-4f6b-b572-9f9e060db134",
  "status": "AwaitingInput",
  "pause": {
    "reason": "tool_approval",
    "stepId": "charge",
    "prompt": null,
    "approvalId": "map_9f2c41d7a8b3"
  },
  "createdAtUtc": "2026-10-17T12:34:56Z"
}
```

`pause.reason` 取值为 `user_input` 或 `tool_approval`。只有 `tool_approval` 会带 `pause.approvalId`，通过下文的审批端点处理。

- **轮询**：再次发送相同 `Idempotency-Key` 且不带续跑 header 时是纯状态查询，只返回当前状态，不会重新进入运行。
- **续跑**：附加 `X-OpenClaw-Resume` header，body 为 JSON 对象：`{"inputs":{"answer":"..."}}`。只有该 header 会触发执行；它不参与请求哈希，因此同一个键仍然代表同一个 invocation。不带 header 的请求是查询；运行已结束时续跑会返回持久化结果。
- **工具审批后的续跑**不携带用户答复。审批通过后，用空 inputs（`{"inputs":{}}`）加续跑 header 发送，运行会从暂停的步骤继续。
- **终态业务失败**以 `Completed` 加 `error` 返回，例如运维拒绝工具步骤时为 `approval.denied`。审批被拒绝会结束本次运行，不会重试。

暂停中的 invocation 已释放执行租约，因此并发的第二次续跑不会启动重复运行。

## 工具审批

当 `OpenClaw:Integration:MetaApprovals:Enabled` 为 `true` 时，MetaSkill 的工具步骤会受审批门禁保护。没有有效授权的受控步骤不会阻塞或自动放行，而是暂停运行并持久化一条审批记录。

```http
GET  /api/integration/meta-approvals?status=pending
POST /api/integration/meta-approvals/{approvalId}
Authorization: Bearer <operator-token>
Content-Type: application/json

{ "approved": true, "reason": "已与值班工程师确认" }
```

| 端点 | 行为 |
| --- | --- |
| `GET /api/integration/meta-approvals` | 列出当前调用方的审批记录。默认只返回待审批记录；加 `status=all` 可包含已决记录，加 `sessionId=<id>` 可缩小范围。 |
| `POST /api/integration/meta-approvals/{approvalId}` | 记录决定（`{"approved":true\|false,"reason":"..."}`）。id 不存在返回 `404`；已决返回 `409 approval.already_decided`；调用方不拥有该记录且非管理员返回 `403 approval.forbidden`。 |

审批 id 是确定性的（`map_<sha256(session|skill|step|tool|argsFingerprint)>`），因此重试同一个暂停步骤会复用同一条记录，而不是生成新的审批请求。授权是一次性的：被消费后，该步骤下次出现时会重新进入待审批。

配置（`OpenClaw:Integration:MetaApprovals`）：

| 键 | 默认值 | 含义 |
| --- | --- | --- |
| `Enabled` | `false` | 关闭时，需要审批的工具保持原有的自动拒绝行为。 |
| `Timeout` | `7.00:00:00` | 未决定的请求在清扫前保持可用的时长。 |
| `GrantTtl` | `00:15:00` | 已批准的授权在被要求重新审批前的可用时长。 |
| `StoragePath` | Gateway `Memory.StoragePath` | 存放审批记录的目录，一条记录一个 JSON 文件。 |

**与交互式审批 API 的边界**：`/api/integration/meta-approvals` 面向机器调用方（DrasiWake 链路）。交互式聊天界面继续使用自己的 `/api/approvals` 端点，它守护的是用户在键盘前实时对话的回合。自动化不要指向 `/api/approvals`，也不要指望集成端点暴露交互式提示。

## 运行可见性（只读）

每次调用都会留下一份事件日志，三个 `GET` 端点把它读出来。三者都是纯读——这里没有恢复、取消或审批——同一份处理器也挂在 `/admin/meta-invocations*` 上供运维看板（`/runs`）使用。集成调用方用下面的机器面，两面返回的载荷逐字节一致。

```http
GET /api/integration/meta-invocations?status=&sessionId=&limit=&cursor=
GET /api/integration/meta-invocations/{invocationId}/events?after=&limit=
GET /api/integration/meta-invocations/{invocationId}/events/stream?after=
Authorization: Bearer <operator-token>
```

| 端点 | 行为 |
| --- | --- |
| `GET /api/integration/meta-invocations` | 调用方自己运行的键集分页，最新在前。`status` 接受 `Running`、`AwaitingInput`、`Completed`、`Uncertain`；`limit` 默认 `50`，上限 `200`。把返回的 `nextCursor` 作为 `cursor` 传回取下一页，分页不会重复或跳过。游标格式错误返回 `400 runs.cursor_invalid`，丢弃它并从第一页重来。 |
| `GET .../{invocationId}/events` | 一页事件：`events`、`nextSeq`、`terminal`、`gap`、`synthetic`。`after` 为排他游标，`nextSeq` 是你可以传入的最大值，更大的 `after` 返回 `400 events.after_invalid`。`limit` 默认 `200`，上限 `1000`。 |
| `GET .../{invocationId}/events/stream` | 服务端推送。每帧是 `event: run_event`，`id: <seq>`，事件 JSON 在 `data:` 里；最后一帧是 `event: terminal`，之后服务端关闭流。用 `Last-Event-ID` 设为已处理的最大 `seq` 重连，会恰好补上漏掉的事件；`Last-Event-ID` 优先于 `after`。 |

访问别人的运行返回 `403 invocation.forbidden`；id 不存在返回 `404 invocation.not_found`。（没有调用存储的宿主上集成整体关闭：`404 invocations.disabled`。）

### 事件

`kind` 是封闭集：`claim`、`resume.requested`、`resumed`、`step.started`、`step.completed`、`step.failed`、`step.paused`、`approval.requested`、`approval.decided`、`completed`、`failed`、`uncertain`。`seq` 每次调用从 `1` 开始，逐条加一。`detail` 是脱敏后的短标签——暂停原因、错误码、截断的审批 id——绝不带步骤输入或输出。

有两个标记，因为日志对它观测不到的东西是诚实的：

- `synthetic: true`——网关没有实时观察到这个边界，是事后从运行记录推导出来的。步骤边界永远是推导的（运行时不会实时抛出），所以 `step.started` / `step.completed` 的时间是观测时间，不能当作耗时读。`step.paused` 和终态事件是真实的。之后再次观测到同一步骤不会重复追加。
- `gap: true`——日志有无法解析的行，或者写不进去。账本仍然前进了，因为运行确实发生过；此时应把时间线视为不完整，而不是重试这次调用。

完全没有日志的运行——早于本特性，或文件丢失——返回整页 `synthetic: true` 的推导时间线，而不是空列表。日志写失败不会回滚状态迁移：运行被标记为 `EventsGap`，读侧报 `gap`。

## 技能索引（只读）

网关把所有可见技能编译成一份带版本的索引，MetaSkill 调用从它提供服务。对集成调用方有两点影响：

```http
GET /api/integration/skill-index
Authorization: Bearer <operator-token>
# scope: integration.read
```

```json
{
  "version": 12,
  "fingerprint": "sha256:...",
  "compiledAtUtc": "2026-10-17T09:12:03Z",
  "ready": true,
  "state": "degraded",
  "lastAttemptVersion": 13,
  "skills": [
    { "name": "incident-review", "kind": "meta", "source": "workspace", "status": "ready" },
    { "name": "broken-skill", "kind": "unknown", "source": "unknown", "status": "invalid", "diagnosticCode": "parse_failed" }
  ],
  "diagnostics": [
    { "path": "/srv/skills/broken-skill/SKILL.md", "code": "parse_failed", "message": "...", "fatal": true }
  ]
}
```

- `version` 是已发布的版本。最近一次编译被**拒绝**时 `lastAttemptVersion` 会更大：被拒的尝试不会替换生效索引（保留最后已知good，LKG），只追加诊断。比较两者即可区分「重载成功」与「保留了上一版」。
- `state` 为 `unready`（`version == 0`，从未发布过可用索引）、`degraded`（可用，但最近一次尝试带诊断）或 `ready`。
- 每个技能的 `status` 为 `ready`、`invalid`（技能目录存在但 `SKILL.md` 解析不了，名称取自目录名）或 `disabled`（配置里关掉）。

**索引不正常时调用。** 索引 `unready` 期间，MetaSkill 调用返回 `503` 与 `{"success": false, "error": "skills.not_ready"}`，且不创建调用记录，因此索引恢复后用同一个 `Idempotency-Key` 重试依然干净。该行为由 `Skills.FailClosedOnUnreadyIndex`（默认 `true`）控制；设为 `false` 则退回索引出现之前的行为。

每次 meta 调用响应还带 `X-OpenClaw-Skill-Index`（`ready` / `degraded` / `unready`），被允许在降级索引上运行的调用方可以记下原因。`GET /health/ready` 在 `skills` 段报告同一状态，并在 `unready` 期间返回 `503`。

## MCP 健康（只读）

网关按间隔用 `tools/list` 探测每个已配置的 MCP server，把结果汇成一个小状态机。两个 `GET` 端点把它暴露出来，让调用方能区分「技能坏了」和「它的某个工具所在 server 挂了」：

```http
GET /api/integration/mcp-health
GET /api/integration/mcp-health/stream
Authorization: Bearer <operator-token>
# scope: integration.read
```

```json
{
  "generatedAtUtc": "2026-10-17T09:12:03Z",
  "servers": [
    { "id": "github", "state": "healthy", "lastProbeAtUtc": "2026-10-17T09:11:58Z", "lastSuccessAtUtc": "2026-10-17T09:11:58Z", "consecutiveFailures": 0, "latencyMs": 42, "toolCount": 27, "lastErrorCode": null },
    { "id": "erp", "state": "degraded", "lastProbeAtUtc": "2026-10-17T09:11:59Z", "lastSuccessAtUtc": "2026-10-17T09:00:12Z", "consecutiveFailures": 2, "latencyMs": null, "toolCount": 8, "lastErrorCode": "timeout" }
  ]
}
```

| 状态 | 含义 | 对工具的影响 |
| --- | --- | --- |
| `unknown` | 已配置但还没探测过（启动、或刚重载进来）。 | 可见（乐观）。 |
| `healthy` | 最近一次探测成功，且工具清单与已注册一致。 | 可见。 |
| `degraded` | 连续 `FailureThreshold`（默认 2）次失败，或工具清单变化。 | 仍可见，调用照常发出。 |
| `down` | 连续 `DownThreshold`（默认 5）次失败。 | 对模型隐藏；调用直接返回 `mcp.server_unavailable`，不发到网络上。 |

恢复需要连续 `RecoveryThreshold`（默认 3）次成功，因此一次侥幸的探测无法把工具在模型工具表里来回抖动。`latencyMs` 是最近一次**成功**探测的耗时，失败期间为 `null`——过期的延迟比没有更糟。

- `lastErrorCode` 是封闭集——`timeout`、`transport`、`protocol`、`unauthorized`、`server_error`、`tools_changed`——绝不是原始异常文本。
- `GET .../stream` 是服务端推送：每次**状态变化**推一帧 `event: mcp_health`，含 `server`、`previousState`、`toolsChanged`、`occurredAtUtc`。流安静就代表集群安静；注释帧（`: keepalive`）只保活，不算状态变化。
- 有 server 不健康时，meta 调用响应带 `X-OpenClaw-Mcp-State: degraded`（或 `down`）；整个集群健康则不带该响应头。它描述的是集群，不是某次运行碰巧用到的 server。
- `Mcp.Health.Enabled` 为 `false` 时不做探测，但集群仍以 `unknown` 列出；此时流按设计保持静默。
- 启动时从未连上的 server 报 `transport`，而不是从列表里消失——会静默少一个 server 的集群视图，比显示一个坏 server 更糟。

配置（`Mcp.Health`）：

| 键 | 默认值 | 含义 |
| --- | --- | --- |
| `Enabled` | `true` | 是否运行探测循环。关闭后仍会连接已配置的 server。 |
| `IntervalSeconds` | `30` | 两轮探测之间的间隔。 |
| `ProbeTimeoutSeconds` | `5` | 单次探测超时，超时计为 `timeout`。 |
| `FailureThreshold` | `2` | 连续失败多少次进入 `degraded`。 |
| `DownThreshold` | `5` | 连续失败多少次进入 `down`（隐藏工具、拒绝调用）。 |
| `RecoveryThreshold` | `3` | 连续成功多少次回到 `healthy`。 |
| `HideUnhealthyTools` | `true` | `down` 的工具是否从模型可见的工具表中移除；无论如何调用都会被拒绝。 |

## 幂等与恢复

调用声明和终态结果保存在已配置的 Gateway `Memory.StoragePath` 下。`Gateway.MetaInvocations.RetentionDays` 控制已完成和不确定记录的保留时间，默认值为 `30` 天。请将该值设为不短于任何调用方的最长重试期限，包括 DrasiWake outbox 的最长重试期限，避免重试发生时去重记录已过期。

如果执行失败、被取消，或进程在持久化终态之前停止，该调用会标记为 `Uncertain`。系统不会自动重试不确定的调用；调用方或运维人员需要先协调确认执行结果，再决定如何继续。使用相同键重试会返回已保存的不确定状态。

该 API 提供持久化去重和结果重放，但不保证在任意进程、机器或外部副作用故障下实现 exactly-once。对于同一个逻辑请求，调用方应在每次重试时保留相同的键；只有确实要发起新的调用时才使用新键。