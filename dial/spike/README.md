# DIAL protocol spike

This folder reproduces the captures behind the guard's golden fixtures
(`dial/guard/tests/fixtures/dial-core-0.50.8/`). Re-run it whenever the pinned DIAL Core or SDK version changes, and
compare the new fixtures with the old ones before trusting the guard on the new version.

Verified on 2026-11-27 with `epam/ai-dial-core:0.50.8`, `redis:7.2.4-alpine3.19`, `aidial-interceptors-sdk 0.9.0`
(`aidial-sdk 0.47.1`).

## Run it

```powershell
# 1. random encryption settings for DIAL Core, from a cryptographic generator (the file is git-ignored)
$hex = { $b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); -join ($b | ForEach-Object { $_.ToString('x2') }) }
Set-Content dial/spike/.env -Encoding ascii -Value "DIAL_ENCRYPTION_SECRET=$(& $hex)", "DIAL_ENCRYPTION_KEY=$(& $hex)"
docker compose -f dial/spike/docker-compose.yml up -d

# 2. echo app (port 7001) and logging interceptor (port 7002) on the host
$py = "dial/guard/.venv/Scripts/python.exe"; $env:DIAL_URL = "http://localhost:8080"
Start-Process $py "dial/spike/echo_app.py dial/spike/captures/echo.jsonl 7001"
Start-Process $py "dial/spike/spike_interceptor.py dial/spike/captures/interceptor.jsonl 7002"

# 3. cases, then fixtures
& $py dial/spike/capture_client.py dial/spike/captures/client.jsonl
& $py dial/spike/capture_round2.py dial/spike/captures/client2.jsonl up
& $py dial/spike/make_fixtures.py dial/spike/captures dial/guard/tests/fixtures/dial-core-0.50.8
```

Some phases of `capture_round2.py` need a process stopped or restarted first:
- `interceptor_down` and `app_down`: stop the matching process, and health-check the other one before sending.
- `conv_proxy`: restart the interceptor with `DIAL_SDK_HEADERS_TO_PROXY=x-conversation-id`.

`settings.json` configures an identity provider that can accept no token: the JWKS address is unreachable, and the
issuer and audience values are impossible. Core needs one provider to start. The captures were recorded with an Entra
provider entry, which API-key traffic never touches.

## What the spike proved

Each fact below comes from a fixture file. The file is named in brackets.

**Starting DIAL Core**
1. Core refuses to start without at least one `identityProviders` entry and without `encryption.secret` and
   `encryption.key`. Traffic that uses API keys never touches the identity provider. With the JVM heap capped at
   384 MB, Core uses about 390 MiB of RAM.

**What an interceptor receives** [`interceptor/block_echo.json`]
2. Core posts to the interceptor's `endpoint` exactly as configured.
3. Headers that arrive:
   - `x-dial-deployment-id`: the deployment the interceptor fronts.
   - `api-key`: a per-request key of 32 characters, not the caller's key.
   - client headers such as `x-conversation-id`.
   - `traceparent`.
4. Headers that do not arrive: no `Authorization` (it is sent only with `forwardAuthToken`), and no user or role headers.

**Interceptor configuration** [`interceptor/block_client_config.json`, `interceptor/nodefaults_client_config.json`]
5. Core sets `custom_fields.interceptor_configuration` from the interceptor's `defaults`.
6. If a client sends its own `interceptor_configuration`, Core replaces it with the defaults. If the interceptor has no
   defaults, Core removes it. So a client cannot change the guard's configuration.

**What the application receives behind an SDK interceptor** [`app/echo__hello_block.json`]
7. The SDK calls Core back at `{DIAL_URL}/openai/deployments/interceptor/chat/completions`, using the per-request key.
8. The application then receives:
   - the per-request key;
   - `x-dial-deployment-features`;
   - `"model": null` in the body, which the SDK adds.
9. The client's `x-conversation-id` is not passed on past an SDK interceptor.

**Identity for applications** [`app/echo-direct__please_whoami.json`, `client/whoami_via_interceptor_adjuster.json`]
10. `GET /v1/user/info` with the per-request key returns the original caller's `roles` and `project`. This works
    directly and through an interceptor, so an application can learn the caller's role from DIAL Core instead of
    trusting a client header.

**Response format**
11. **Non-streaming through an interceptor** [`interceptor/block_echo.json`]: the SDK turns the upstream response into
    one chunk with `delta`. It then merges that chunk back into one response for the client, with new `id` and
    `created` values and `model` set to the deployment id.
12. **Streaming** [`client/stream_echo.json`]:
    - each chunk is sent as `data: {compact json}`, and the stream ends with `data: [DONE]`;
    - content type `text/event-stream; charset=utf-8`, gzip-encoded;
    - `tool_calls` and `custom_content` stages and attachments arrive as indexed deltas [`client/stream_toolcall.json`,
      `client/stream_attach.json`].

**Failures**
13. **App fails with HTTP 500 before streaming:** the client gets HTTP 500 with the app's JSON error, in both stream
    and non-stream mode [`client/stream_app500.json`].
14. **App fails mid-stream:** the client gets HTTP 200. The chunks sent so far are followed by
    `data: {"error": ...}` and then `data: [DONE]` [`client/stream_app_midstream_error.json`].
15. **Interceptor raises an exception:**
    - before its first chunk, the client gets HTTP 500, and the message contains the raw exception text
      [`client/block_break_before.json`];
    - mid-stream, the chunks already sent stay with the client, and then the error event arrives
      [`client/stream_break_mid.json`];
    - **so the guard sends sanitized errors and holds back unchecked text.**
16. **Interceptor down:** HTTP 502 `BAD_GATEWAY` "Failed to connect to upstream server". The app never receives the
    request, so Core fails closed [`client/block_interceptor_down.json`].
17. **App down:** the same 502. The interceptor receives the request and passes Core's 502 through unchanged
    [`client/block_app_down.json`, `interceptor/block_app_down.json`].
18. **Unknown key:** 401 "Unknown api key". **Unknown deployment:** 404 "Unknown deployment: …".

**Which client headers reach whom**
19. **Without an interceptor:** Core passes client headers to the application unchanged. This includes
    `X-Dev-Persona` and even spoofed `X-DIAL-Deployment-Id` and `X-Upstream-Key` values
    [`app/echo-direct__persona_header_direct.json`]. So an application behind DIAL must not trust any request header
    for identity. It asks `/v1/user/info` instead.
20. **Through an SDK interceptor:** the interceptor sees the client's headers, but Core overwrites
    `x-dial-deployment-id` with its own value. The application receives none of the client's headers
    [`interceptor/persona_header_via_interceptor.json`, `app/echo__persona_header_via_interceptor.json`].
21. **Conversation id:** with `DIAL_SDK_HEADERS_TO_PROXY=x-conversation-id` on the interceptor, the conversation id
    reaches the application again [`app/echo__conversation_id_proxied.json`].
22. **Slow first request:** the first request after an interceptor starts took 15 to 21 s on this Windows host. Later
    requests took under 1 s. Timing measurements need a warm-up request first.

`/v1/user/info` was verified with API keys only. Calls made with a user's Entra token have not been tested yet.
