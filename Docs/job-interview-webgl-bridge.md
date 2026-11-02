# Job-interview WebGL bridge (Stage 6B) — local setup

The WebGL build talks to exactly one Blazor parent page (Stage 6A) over a versioned
`postMessage` bridge. The Blazor parent owns sign-in, the bearer token, the Recursor session,
and every HTTP call; Unity never receives, stores, logs, or sends a token. The wire protocol is
defined by the Recursor repository's `docs/contracts/job-interview-conversation-v1.md` §14.1 and
the committed Stage 6A files, which are authoritative.

## 1. Two different origins

| Name | What it is | Where it is configured |
|---|---|---|
| **Parent (Blazor) origin** — `allowedHostOrigin` here, `#parentOrigin` in the iframe URL | The origin of the Blazor/Recursor application page that embeds the iframe and exchanges `postMessage` with Unity. | Unity: the git-ignored settings asset below. Stage 6A derives `#parentOrigin` from its own page address automatically. |
| **Unity content origin** — Stage 6A `UnityOrigin` / `UnityBuildUrl` | Where the Unity WebGL files (`index.html`, `Build/…`) are served — the iframe's own origin. | Blazor client `wwwroot/appsettings.{Environment}.json` → `RecursorInterviewHost:UnityOrigin` and `RecursorInterviewHost:UnityBuildUrl` (git-ignored there). |

These are usually **different** origins. `allowedHostOrigin` must be the **parent Blazor
origin**, never the origin serving the Unity files — unless the two applications are
intentionally deployed same-origin, in which case both values are the same.

What each side checks:

- The Blazor parent (Stage 6A) posts only to `UnityOrigin` and accepts messages only from the
  iframe's window at `UnityOrigin`.
- The Unity `.jslib` (Stage 6B) enables itself only when framed and when the iframe URL fragment
  is exactly `#parentOrigin=<allowedHostOrigin>&bridgeVersion=1`. It then accepts messages only
  when `event.source === window.parent` and `event.origin === allowedHostOrigin` (the parent
  Blazor origin), and posts only with `targetOrigin = allowedHostOrigin` — never `"*"`. The
  fragment is never trusted by itself: it must equal the value baked into the build.

### Local example (two origins)

| | Value |
|---|---|
| Blazor parent (Server `https` launch profile) | `https://localhost:7279` |
| Unity build served by any static HTTPS server | `https://localhost:8443` (an example; any port) |

- Unity settings asset: `allowedHostOrigin: https://localhost:7279`,
  `allowLoopbackDevelopmentOrigin: 1`, and build a **Development** build (a Release build
  rejects a loopback parent even with the flag).
- Blazor `Client/wwwroot/appsettings.Development.json` (git-ignored):
  `"RecursorInterviewHost": { "UnityOrigin": "https://localhost:8443", "UnityBuildUrl": "https://localhost:8443/index.html" }`.
- Run the Blazor app with the **https** profile. `http://localhost:5010` is a different origin
  and is never accepted as `allowedHostOrigin`.
- Stage 6A permits an `http://` `UnityOrigin` only in Development; an https parent framing
  http content can also be blocked as mixed content, so prefer https for the Unity server too.
- The Unity static server must send the `Content-Encoding`/MIME headers Unity documents for
  Gzip builds, or rely on the build's decompression fallback (enabled).

## 2. Parent-origin settings asset (required, never committed)

The value lives in a ScriptableObject that is **git-ignored by exact path**:

```text
Assets/JobInterview/Resources/RecursorInterviewBridgeSettings.asset
```

Create it with **Job Interview › Create Local Bridge Settings Asset**, then set its fields in the
Inspector. Field format (placeholders, not a real deployment):

```yaml
allowedHostOrigin: https://interview-host.example        # the PARENT Blazor origin
allowLoopbackDevelopmentOrigin: 0                        # 1 only for a local loopback parent (Development builds)
```

`allowedHostOrigin` rules — anything else disables the bridge and fails a WebGL build:

- `https://host` or `https://host:port`, exactly as the browser serializes the parent page's
  origin: lowercase host; no path, query, fragment, credentials, wildcard, trailing slash,
  whitespace, or explicit default port (`:443`). `http://` is never accepted.
- A non-loopback https parent origin is valid in Development and Release builds.
- A loopback parent (`https://localhost:<port>`, `https://127.0.0.1:<port>`, `*.localhost`) is
  accepted only when `allowLoopbackDevelopmentOrigin` is set **and** the build is a Development
  build (or validation runs in the Editor). A non-Development Release build rejects loopback even
  with the flag, so a mistakenly configured production build can never trust localhost.

## 3. Build version (`simVersion`)

`bridge.hello` sends `Application.version` as `simVersion`; the Blazor parent passes it to
`sessions/start` as `SimVersion`. **Player Settings › Version** (`bundleVersion`) must be a
nonblank, project-controlled value: 1–64 characters, an ASCII letter or digit first, then only
letters, digits, `.`, `_`, `+`, `-`. There is no fallback value; an invalid version disables the
bridge and fails the WebGL build. Any later telemetry batch for the same session must use the
same `SimVersion` (the server compares it exactly).

## 4. Build settings enforced by `InterviewWebGLBuildValidator`

A WebGL build fails with a fixed, value-free message unless:

- `Assets/Scenes/JobInterview.unity` is enabled in Build Settings (it is the only enabled scene);
- WebGL compression is **Gzip** with **Decompression Fallback** enabled (contract §14.1);
- the version and settings asset above are valid.

## 5. Provider selection

`InterviewSessionController.providerMode` defaults to **Automatic**: the Blazor-parent bridge in
a WebGL player, the scripted provider (unchanged, for offline/editor use) everywhere else. Set it
to **ScriptedLocal** to force the scripted interview in a WebGL build.

## 6. Observation telemetry (telemetry lane)

Governed by the Recursor repository's `docs/contracts/job-interview-telemetry-v1.md` (merged in
PR #31, master `47cf7ff`). Host-bridge mode only; the scripted provider sends nothing.

**What Unity sends.** `InterviewTelemetrySender` (in `Scripts/Telemetry/`) turns
`InterviewBehaviorCollector` observations into the five contract events:

- `interview_turn_presented`, `interview_response_started`, `interview_clarification_requested`,
  `interview_response_submitted` and `interview_turn_completed`;
- metrics: `turnIndex`, plus whole-millisecond `responseLatencyMs` / `responseDurationMs` only when
  observed and within 0–600000;
- payload: `responseLengthBand` (a band, never a count or text), and on a completed turn
  `topicCompleted` (and `completionStatus` only as below).

It never sends `interview_response_abandoned` or `interview_interruption_recorded`, and it never
infers abandonment.

**`topicCompleted` is structural (P1).** It is `true` when the next prompt has a different
`questionRef` (a new core question) or the interview genuinely completed, and `false` for a
follow-up on the same question. It is never a judgment of the answer.

**`completionStatus` (P3).** It is `"completed"` only on the last completed turn of a genuinely
completed interview. It is never sent on an ordinary turn, an explicit end, a restart, an expiry,
an interruption or an error.

**Batches and order.**

- One telemetry run per conversation, from its first prompt.
- A batch is cut right after each `interview_turn_completed`. When the conversation ends, or the
  screen is reset, any remaining events form one final batch; an empty batch is never sent.
- Batches go out one at a time in `BatchSequence` order (from 0 per run). `telemetry.finish
  {conversationId, lastBatchSequence}` (-1 when none) goes only after every batch has a result. A
  later run waits for the earlier run's finish.

**Run conversation id (contract §3.4).**

- Every `telemetry.batch` and `telemetry.finish` carries `conversationId` as the first payload field.
  It is the server `ConversationId` that the host returned for the start of the run's own
  conversation, the same opaque value the conversation lane already sends with
  `answer`/`clarify`/`end`. The host routes each message to that conversation's own Recursor session
  and never forwards the id to the server. It is never part of an event.
- **Bound once.** The controller passes
  `HostBridgeInterviewConversationProvider.StartedConversationIdFor(firstTurn)` to `BeginRun`. That id
  comes from the reply to the start that produced exactly that first turn, never from the active
  conversation.
- **Never replaced.** Resends, finishes, runs queued behind an earlier run, and an earlier run still
  delivering after a reset all keep their run's own id.
- **No valid id, no telemetry.** A run without a valid id records and sends nothing. It is
  `Incomplete` with `telemetry_no_conversation`. The builders and the bridge client also refuse an
  invalid id, so nothing is posted.

**Retries (Unity-owned, P4).**

- After an ambiguous outcome — `transport_failure`, `busy`, no reply within 75 s, or not posted —
  the *same* batch (same `BatchId`, `BatchSequence`, event ids and bytes) is resent at most twice,
  after 1 s and then 3 s. It is then left unacknowledged and the next batch follows.
- A definitive `rejected` is never resent. `forbidden`, `authentication_required` or
  `not_permitted` stops the run: no further batch and no finish.
- An `incomplete` finish caused by a failed end (for example `terminal_window_not_submitted`) or a
  transport failure is resent on the same schedule. One caused by `bridge_batch_not_acknowledged`
  or `bridge_batch_rejected` is final.

**Outcomes.** `InterviewSessionController.TelemetryStatus` / `TelemetryStatusCode`:

- `Ended`: the host acknowledged every batch and ended the Recursor session. It does **not**
  confirm the data is stored or queryable in ADX (the server uses queued ingestion).
- `Incomplete`: telemetry is not known to be complete, with a fixed reason code. Nothing is
  reported as saved.

The interview UI is unchanged and shows no telemetry status.

**Boundaries.** The telemetry lane never blocks or changes a conversation operation, and a
conversation reset does not cancel it. No message carries answer, clarification or interviewer
text, a token, a learner key, a server `SessionId`, `UserId`, `SimId`, `ScenarioId` or `Context`:
the host adds those from its own session. Payload values are enum members or booleans only, and
`BuildTelemetryBatch` refuses anything else before posting.

**Host compatibility.**

- Requires a host with the Recursor contract §3.4 amendment: the host-session lifecycle change on the
  Recursor branch `fix/recursor-interview-host-session-lifecycle`, not merged or deployed at the time of
  writing. Release the amended host before any build with this sender is used.
  - **Hosts at `47cf7ff` before §3.4** refuse these messages, because they don't expect `conversationId`.
  - **Older hosts** (for example the currently deployed `38604a5`) silently drop `telemetry.*` messages.
  - **In both cases**, the run ends `Incomplete` and the interview itself is unaffected.

**Tests.** `Tests/Editor/InterviewTelemetrySenderTests.cs` (EditMode). One test briefly creates an
`InterviewScenarioDefinition` to drive the host-backed provider, and destroys it afterwards.
