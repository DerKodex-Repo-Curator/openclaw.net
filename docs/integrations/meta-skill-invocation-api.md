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

## Run visibility (read-only)

Every invocation keeps a journal of what happened to it, and three `GET` endpoints expose it. All three are pure reads — there is no resume, cancel or approve here — and the identical handlers are also mounted on `/admin/meta-invocations*` for the operator dashboard (`/runs`). Integration callers use the machine face below; the two faces return byte-identical payloads.

```http
GET /api/integration/meta-invocations?status=&sessionId=&limit=&cursor=
GET /api/integration/meta-invocations/{invocationId}/events?after=&limit=
GET /api/integration/meta-invocations/{invocationId}/events/stream?after=
Authorization: Bearer <operator-token>
```

| Endpoint | Behaviour |
| --- | --- |
| `GET /api/integration/meta-invocations` | Keyset page of the caller's own runs, newest first. `status` accepts `Running`, `AwaitingInput`, `Completed`, `Uncertain`; `limit` defaults to `50` and caps at `200`. Pass the returned `nextCursor` back as `cursor` for the next page — pages never repeat or skip a row. A malformed cursor returns `400 runs.cursor_invalid`, so discard it and restart from the first page. |
| `GET .../{invocationId}/events` | One page of events: `events`, `nextSeq`, `terminal`, `gap`, `synthetic`. `after` is exclusive; `nextSeq` is the highest sequence you may pass, and a larger `after` returns `400 events.after_invalid`. `limit` defaults to `200` and caps at `1000`. |
| `GET .../{invocationId}/events/stream` | Server-sent events. Each frame is `event: run_event` with `id: <seq>` and the event JSON as `data:`; the last frame is `event: terminal`, after which the server closes the stream. Reconnect with `Last-Event-ID` set to the last `seq` you processed and you receive exactly the events you missed. `Last-Event-ID` takes precedence over `after`. |

A run owned by another caller returns `403 invocation.forbidden`; an unknown id returns `404 invocation.not_found`. (Integration is disabled entirely on a host without the invocation store: `404 invocations.disabled`.)

### Events

`kind` is a closed set: `claim`, `resume.requested`, `resumed`, `step.started`, `step.completed`, `step.failed`, `step.paused`, `approval.requested`, `approval.decided`, `completed`, `failed`, `uncertain`. `seq` starts at `1` for each invocation and increases by one per event. `detail` is a short redacted label — a pause reason, an error code, a truncated approval id — and never carries step input or output.

Two flags exist because the journal is honest about what it cannot observe:

- `synthetic: true` — the gateway did not watch that boundary live and derived the event from the run record afterwards. Step boundaries are always synthetic (the runtime does not surface them as they happen), so the timestamps on `step.started` / `step.completed` are observation times and must not be read as durations. `step.paused` and the terminal event are real. Re-deriving the same step on a later observation does not append it twice.
- `gap: true` — the journal has unreadable lines, or could not be written. The ledger still advanced, because the run really happened; treat the timeline as incomplete rather than retrying the invocation.

A run with no journal at all — it predates this feature, or the file was lost — answers with a timeline derived from the run record and `synthetic: true` on the whole page, instead of an empty list. A journal write failure never rolls back a state transition: the run is marked `EventsGap` and the read side reports `gap`.

## Skill index (read-only)

The gateway compiles every visible skill into one versioned index and serves MetaSkill invocations
from it. Two things follow for integration callers:

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

- `version` is the published version. `lastAttemptVersion` is newer when the most recent compile was
  **rejected**: a rejected attempt never replaces the active index (last known good), it only adds
  diagnostics. Comparing the two is how you tell "reloaded" from "kept the previous index".
- `state` is `unready` (`version == 0`, nothing usable has ever been published), `degraded` (usable,
  but the latest attempt reported diagnostics) or `ready`.
- `status` per skill is `ready`, `invalid` (the skill directory exists but its `SKILL.md` could not
  be parsed — the name is taken from the directory) or `disabled` (turned off by config).

**Calling during a bad index.** While the index is unready, a MetaSkill invocation answers
`503` with `{"success": false, "error": "skills.not_ready"}` and creates no invocation record, so a
retry with the same `Idempotency-Key` is still clean once the index recovers. `Skills.FailClosedOnUnreadyIndex`
(default `true`) controls that; with `false` the call proceeds as it did before the index existed.

Every meta-invocation response also carries `X-OpenClaw-Skill-Index` (`ready`, `degraded` or
`unready`), so a caller that was allowed to run against a degraded index can record why. `GET
/health/ready` reports the same state in a `skills` section and returns `503` while unready.

## Idempotency and Recovery

Invocation claims and terminal results are stored under the configured Gateway `Memory.StoragePath`. `Gateway.MetaInvocations.RetentionDays` controls how long completed and uncertain records are retained; it defaults to `30` days. A record's event journal is deleted together with the record, so an expired run does not leave an orphaned `meta-invocations/*.events.ndjson` behind. Set retention to at least the longest retry window of any caller, including the DrasiWake outbox, so an old retry cannot outlive its deduplication record.

If execution fails, is cancelled, or a process stops before persisting a terminal result, the invocation is marked `Uncertain`. An uncertain invocation is not automatically retried; the caller or operator must reconcile its outcome before choosing how to proceed. Retrying the same key reports the stored uncertain state.

This API provides durable deduplication and replay, not an exactly-once guarantee across arbitrary process, machine, or external side-effect failures. A caller should preserve the same key for every retry of one logical request and use a new key only for a deliberately new invocation.