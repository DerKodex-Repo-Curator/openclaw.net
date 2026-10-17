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

## 幂等与恢复

调用声明和终态结果保存在已配置的 Gateway `Memory.StoragePath` 下。`Gateway.MetaInvocations.RetentionDays` 控制已完成和不确定记录的保留时间，默认值为 `30` 天。请将该值设为不短于任何调用方的最长重试期限，包括 DrasiWake outbox 的最长重试期限，避免重试发生时去重记录已过期。

如果执行失败、被取消，或进程在持久化终态之前停止，该调用会标记为 `Uncertain`。系统不会自动重试不确定的调用；调用方或运维人员需要先协调确认执行结果，再决定如何继续。使用相同键重试会返回已保存的不确定状态。

该 API 提供持久化去重和结果重放，但不保证在任意进程、机器或外部副作用故障下实现 exactly-once。对于同一个逻辑请求，调用方应在每次重试时保留相同的键；只有确实要发起新的调用时才使用新键。