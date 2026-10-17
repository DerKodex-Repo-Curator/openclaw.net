# MetaSkill Invocation API

`POST /api/integration/meta-invocations` directly executes one explicitly named MetaSkill DAG in the existing session. It does not submit a chat message or start an external workflow.

## Authentication

Use the Gateway's normal operator authentication with the `integration.mutate` permission. A Bearer operator token can be sent in the `Authorization` header. Browser-session requests must also send the `X-CSRF-Token` header returned at sign-in. The endpoint applies the existing operator authorization and rate-limit checks before reading the idempotency key or request body.

The caller must be allowed to write to the requested existing session. Session ownership is checked before looking up or claiming the idempotency key, so a denied request does not consume that key.

## Request

Send a unique `Idempotency-Key` header (maximum 200 characters) and a JSON body with all three fields:

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

`skill` and `sessionId` must be non-empty. `input` may be a string or `null`. The key is scoped to the authenticated caller; a different caller may use the same key independently. Reusing a key with the same `skill`, `input`, and `sessionId` returns the original invocation state or completed result. Reusing it with a different request returns a conflict.

## Responses

| HTTP status | Meaning |
| --- | --- |
| `200 OK` | The invocation completed, or the run is paused waiting for a human (`AwaitingInput`, see below). A replay returns the persisted result. |
| `202 Accepted` | The same request is still running. It includes the original `invocationId` and `Running` status; retry the same request with the same key to check again. |
| `409 Conflict` | Either the key was used for a different request, or the invocation is `Uncertain`. A key conflict returns an error object; an uncertain invocation returns its `invocationId`, `Uncertain` status, and error. |

Completed response example:

```json
{
  "invocationId": "3a1d3589-2856-4f6b-b572-9f9e060db134",
  "status": "Completed",
  "result": "The next action is to rotate the affected credential.",
  "createdAtUtc": "2026-10-02T12:34:56Z"
}
```

Invalid JSON or missing fields return `400 Bad Request`; a missing session returns `404 Not Found`; insufficient session access returns `403 Forbidden`.

## Paused runs

A run can stop and wait for a human — either for free-form input (`user_input` step) or for a tool approval (`tool_approval`). The invocation then reports `AwaitingInput` with HTTP `200 OK` and a `pause` descriptor:

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

`pause.reason` is `user_input` or `tool_approval`. `pause.approvalId` is present for `tool_approval` only; decide it through the approval endpoints below.

- **Polling.** Sending the same `Idempotency-Key` again without a resume header is a pure status query: it returns the current state and never re-enters the run.
- **Resuming.** Add the `X-OpenClaw-Resume` header with a JSON object body: `{"inputs":{"answer":"..."}}`. Only this header triggers execution; it is not part of the request hash, so the same key still denotes the same invocation. A resume without the header is a query, and a resume whose run already finished returns the persisted result.
- **Resume after a tool approval** carries no user answer. Once the approval is granted, send the resume header with empty inputs (`{"inputs":{}}`) and the run continues from the paused step.
- **Terminal business failure** is reported as `Completed` with an `error` object, for example `approval.denied` when an operator rejected the tool step. A denied approval ends the run; it is not retried.

A paused invocation keeps its execution lease released, so a second concurrent resume cannot start a duplicate run.

## Tool approvals

Meta-skill tool steps are gated when `OpenClaw:Integration:MetaApprovals:Enabled` is `true`. A gated step that has no valid grant parks the run and stores a durable approval record instead of blocking or auto-approving it.

```http
GET  /api/integration/meta-approvals?status=pending
POST /api/integration/meta-approvals/{approvalId}
Authorization: Bearer <operator-token>
Content-Type: application/json

{ "approved": true, "reason": "verified with the on-call engineer" }
```

| Endpoint | Behaviour |
| --- | --- |
| `GET /api/integration/meta-approvals` | Lists the caller's approval records. Returns pending records by default; add `status=all` to include decided ones, and `sessionId=<id>` to narrow the list. |
| `POST /api/integration/meta-approvals/{approvalId}` | Records the decision (`{"approved":true\|false,"reason":"..."}`). `404` when the id is unknown, `409 approval.already_decided` when it was already decided, `403 approval.forbidden` when the caller does not own the record and is not an admin. |

Approval ids are deterministic (`map_<sha256(session|skill|step|tool|argsFingerprint)>`), so retrying a parked step reuses the same record instead of creating a new prompt. A grant is single-use: once consumed, the next occurrence of the same step parks again.

Configuration (`OpenClaw:Integration:MetaApprovals`):

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | When off, approval-required tools keep the previous auto-deny behaviour. |
| `Timeout` | `7.00:00:00` | How long an undecided request stays open before it is swept. |
| `GrantTtl` | `00:15:00` | How long an approved grant stays usable before it must be re-approved. |
| `StoragePath` | Gateway `Memory.StoragePath` | Directory holding one JSON file per approval record. |

**Boundary with the interactive approval API.** `/api/integration/meta-approvals` is machine-facing and belongs to integration callers (the DrasiWake path). The interactive chat surfaces keep using their own `/api/approvals` endpoints, which gate live user turns for a human at the keyboard. Do not point automation at `/api/approvals`, and do not expect the integration endpoints to surface interactive prompts.

## Idempotency and Recovery

Invocation claims and terminal results are stored under the configured Gateway `Memory.StoragePath`. `Gateway.MetaInvocations.RetentionDays` controls how long completed and uncertain records are retained; it defaults to `30` days. Set it to at least the longest retry window of any caller, including the DrasiWake outbox, so an old retry cannot outlive its deduplication record.

If execution fails, is cancelled, or a process stops before persisting a terminal result, the invocation is marked `Uncertain`. An uncertain invocation is not automatically retried; the caller or operator must reconcile its outcome before choosing how to proceed. Retrying the same key reports the stored uncertain state.

This API provides durable deduplication and replay, not an exactly-once guarantee across arbitrary process, machine, or external side-effect failures. A caller should preserve the same key for every retry of one logical request and use a new key only for a deliberately new invocation.