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
