# Management API

The MVC UI and external clients use `/api/v1`. Endpoints delegate to the same application services, validation, and optional-auth policy. JSON field names use camelCase. DTOs are in `Api/Contracts/`.

## Implemented endpoints

| Method | Route | Behavior |
| --- | --- | --- |
| GET | `/api/v1/authentication/session` | Anonymous bootstrap: auth mode, current sign-in state, identifier when signed in, and CSRF token |
| POST | `/api/v1/authentication/enable` | Set main-account credentials and enable auth; 409 if already enabled; requires a subsequent sign-in |
| POST | `/api/v1/authentication/login` | Username/email and password, plus `twoFactorCode` when enabled; sets a cookie only after both factors pass; 204 |
| POST | `/api/v1/authentication/logout` | Clear the caller's session cookie; returns 204 |
| POST | `/api/v1/authentication/disable` | Disable auth and sign out; requires sign-in while auth is enabled |
| GET | `/api/v1/settings/app` | Read `authenticationEnabled`, `publicDashboardEnabled`, `showPublicDomains`, `publicDashboardMessage`, and `publicDashboardTitle` |
| PUT | `/api/v1/settings/app` | Save public visibility, domain visibility, and the welcome title/paragraph; authentication stays unchanged |
| GET | `/api/v1/public/dashboard` | Anonymous welcome-page data: `title`, `message`, and allowed `domains`, or 404 when disabled |
| GET | `/api/v1/authentication/two-factor` | Account cookie required: `isEnabled` and `recoveryCodesRemaining` |
| POST | `/api/v1/authentication/two-factor/setup` | Account cookie and `password`: return `secret`, `authenticatorUri`, and ten-minute `setupToken` |
| POST | `/api/v1/authentication/two-factor/confirm` | Account cookie, `setupToken`, and six-digit `code`: enable 2FA, return eight `recoveryCodes` once, sign out sessions |
| POST | `/api/v1/authentication/two-factor/disable` | Account cookie, `password`, and authenticator/recovery `code`: disable 2FA and sign out sessions; 204 |
| GET | `/api/v1/system/metrics` | Read available CPU cores, total RAM/disk bytes, and usage percentages; unavailable values are null |
| GET | `/api/v1/settings/proxy` | Read configured `listenPort`, actual `listeningPort`, and nullable `listenerError` |
| PUT | `/api/v1/settings/proxy` | Save `listenPort`, 1–65535, and apply the listener change |
| GET | `/api/v1/proxies?search=...` | List configured proxies; optional case-insensitive domain/destination search |
| GET | `/api/v1/proxies/{id}/events` | Latest 20 saved player login attempts for this proxy, newest first; 404 if the proxy is missing |
| GET | `/api/v1/proxies/{id}/connections` | Currently open named forwarding sessions, held in memory, newest first; 404 if the proxy is missing |
| POST | `/api/v1/proxies` | Create a domain route; returns 201, or 409 for a duplicate domain or destination host/port pair |
| PUT | `/api/v1/proxies/{id}` | Save domain/destination and replace only this live route; 200, 404 if missing, 409 for a duplicate domain or destination host/port pair |
| DELETE | `/api/v1/proxies/{id}` | Delete the saved route and its connection history, and close only its connections; 204, or 404 if missing |
| GET | `/api/v1/settings/api-key` | Read `isConfigured` and display `keyPrefix`; never returns the secret or hash |
| POST | `/api/v1/settings/api-key` | Generate or replace the single API key; returns `apiKey` and `keyPrefix` once |

Once an API key is configured, external management requests require a valid `X-API-Key`, including session bootstrap and login. The explicit public dashboard endpoint is anonymous and exposes the configured welcome title/paragraph and opted-in names only. Two-factor endpoints require an authenticated account cookie; a key alone cannot enroll or remove a factor. Invalid keys on management endpoints return **401**. Before a key is configured, the previous cookie/session flow remains available.

The SignalR hub at `/api/v1/hubs/domains` requires an API key even when dashboard login is off. It emits `DomainAdded` and `DomainRemoved` with `proxyId`, `domain`, and UTC `occurredAt`. Renaming emits both. See the [event guide](DOMAIN_EVENTS.md) for the C# integration example, authenticated transports, key rotation, and snapshot reconciliation after reconnecting.

See [authentication and public visibility](AUTHENTICATION.md) for 2FA setup, recovery codes, protected storage, and public-page behavior. Authenticator setup/removal and login share the existing IP rate limiter.

The dashboard uses its valid CSRF token/cookie pair instead of the API key, including for GET requests, and follows the optional login setting. This is a session-based distinction, not detection of browser software: when login is off, anyone can open the dashboard and obtain its anonymous session. Enable dashboard login to restrict that path. A custom UI header, Origin, or Referer alone does not grant the exemption.

MVC pages redirect to sign-in when required; unauthorized API requests return **401** rather than login HTML. Invalid input returns **400**, duplicate/conflicting operations **409**, and excessive login attempts **429**. Application/validation errors use ProblemDetails; framework CSRF/rate-limit responses may have no JSON body.

## API key and reference

Open **App Settings → API access** to generate the app's single key. The raw key is returned/displayed once; only its SHA-256 hash and a display prefix are stored in `api_keys`. Regeneration updates the same row and invalidates the old key immediately. Keys grant full management access; keep them private. The UI does not store or send this key automatically.

External clients send the header on every call:

```bash
curl -H "X-API-Key: $PROXYCRAFT_API_KEY" https://your-dashboard/api/v1/proxies
```

For POST/PUT, also send `Content-Type: application/json` with the request body. Do not put the key in a URL. No cookies, CSRF bootstrap, or login request is needed with a valid key.

**App Settings → Open API reference** opens `/settings/api`. This C#/Razor page shows every management endpoint with path/query parameters, request fields and validation, response codes, output fields, and illustrative JSON. It follows dashboard sign-in rules and supports Light, Dark, and Rainbow. The page is informational and does not execute API operations. Routes and fields come from existing MVC/DTO metadata; it needs no documentation packages. The former `/api-docs` assets and `/api/openapi/v1.json` route are no longer served.

## Cookies and CSRF

The dashboard uses this flow; it also remains available to clients before an API key is configured:

1. GET `/api/v1/authentication/session`, preserving response cookies. Read `csrfToken` from the JSON response.
2. Include that token in the `X-CSRF-TOKEN` header with the cookies. Dashboard requests include it on GET as well as mutations; once a key is configured, API bootstrap must start from a dashboard page's token or use the key.
3. When auth is enabled, POST the credentials to `/api/v1/authentication/login`. Preserve the returned session cookie.
4. Fetch `/api/v1/authentication/session` again after signing in to obtain the token bound to the authenticated identity.
5. Call the management endpoints with those cookies and the current token. Refresh the token after any identity change.

The same browser CSRF requirement applies when authentication is off, including first-account setup. A token alone does not authorize a protected operation. Only successfully authenticated API-key requests are exempt from mutation CSRF checks. There are no bearer-token endpoints.

## Request examples

### System resource readings

GET `/api/v1/system/metrics` returns:

```json
{
  "totalCpuCores": 4,
  "cpuUsagePercent": 12.5,
  "totalMemoryBytes": 8589934592,
  "memoryUsagePercent": 35.2,
  "totalDiskBytes": 68719476736,
  "diskUsagePercent": 42.1
}
```

These are guest/container resources available to ProxyCraft, rather than process-only CPU or working-set memory. CPU and RAM monitoring use Linux proc/cgroup counters. Visible cgroup v1/v2 quotas, inherited limits, and cpusets constrain CPU capacity; fractional quotas can produce values such as `1.5` cores. CPU usage is normalized to that capacity and requires two samples. Guest RAM usage uses `MemTotal - MemAvailable`; container RAM uses its charged memory (including cache) and memory limit. Unreadable counters never substitute host-wide usage against a container limit.

Disk refers to the filesystem containing the configured app-data directory, not a sum of all disks or Proxmox storage pools. Used space is total minus filesystem free space. Samples refresh in the background every 2 seconds; Home polls every 5 seconds. Percentages range from 0 to 100; missing, unsupported, and not-yet-sampled values are null. CPU/RAM usage is unavailable on non-Linux hosts; disk uses .NET filesystem information. The endpoint follows the existing dashboard/API-key rules and is listed in the in-app reference.

### Player connection history

GET `/api/v1/proxies/1/events` returns up to 20 entries, newest first:

```json
[
  {
    "id": 42,
    "playerName": "Steve",
    "domain": "play.example.com",
    "createdAt": "2026-01-01T12:00:00+00:00"
  }
]
```

An existing proxy with no attempts returns `[]`; a missing proxy returns **404**. The usual session/API-key rules apply. Events are created by Minecraft login traffic, with no event-creation endpoint. They store the client-supplied username, requested domain at that moment, and UTC time. They do not confirm identity or successful joining. Status pings and incomplete/invalid login packets create no events; a valid attempt is saved before the backend connection, including when that backend is unavailable.

Storage is capped at 20 rows per proxy using an atomic insert/prune transaction. Editing a proxy preserves its history and historical domains; deleting it cascades to its events. Recreating the same domain starts with empty history. The history dialog refreshes this endpoint every 5 seconds while open and visible.

### Live player connections

GET `/api/v1/proxies/1/connections` returns current sessions, newest first:

```json
[
  {
    "id": 42,
    "playerName": "Steve",
    "domain": "play.example.com",
    "connectedAt": "2026-01-01T12:00:00+00:00"
  }
]
```

An existing proxy with no live sessions returns `[]`; a missing proxy returns **404**. The endpoint follows the usual session/API-key rules and sends `Cache-Control: no-store`. There are no write operations for live sessions.

Entries appear after the initial named login packet is forwarded to an open backend connection. They remain only in process memory and are removed on socket closure/error, route replacement/deletion, or shutdown. Restarting clears the live list while preserving saved history. `id` identifies a connection within this process lifetime: duplicate player names remain separate sessions. `connectedAt` is UTC. Status pings, pending logins, and failed backend connections do not appear. The list is not capped at 20; that cap applies only to saved attempts.

An open session does not prove authentication or a successful Minecraft login. Exact crash/kick reasons are not inferred, and a silent loss can remain visible until TCP detects it. The monitor/activity button refreshes the list every 2 seconds while its dialog is open and the tab is visible; the history button continues using the events endpoint every 5 seconds. Both dialogs keep their controls visible above a scrolling list and preserve the reading position on updates. The live list has been tested with 100 simultaneous sessions; see [capacity checks](CAPACITY.md) for measurements and limits.

### Authentication and proxy requests

Enabling auth or signing in uses a **single** identifier field. A username such as `admin` is also valid:

```json
{
  "usernameOrEmail": "owner@example.com",
  "password": "a-password-you-choose"
}
```

Identifiers must be 3–254 characters without spaces, are matched case-insensitively, and passwords must be 12–128 characters. Only password hashes are stored. Enabling auth returns `{"authenticationEnabled":true}` and does not sign in automatically.

Create a proxy, or edit one with PUT `/api/v1/proxies/{id}` using the same body:

```json
{
  "domain": "play.example.com",
  "destinationHost": "192.168.1.10",
  "destinationPort": 25565
}
```

`destinationPort` defaults to **25565** when omitted. Destinations support DNS names, IPv4, and IPv6; enter only the host/IP, without URL schemes or embedded ports. Route domains are normalized to lowercase without a trailing dot and must be unique and contain at least one meaningful dot. `play.example.com` and `server.local` are accepted; `servername`, `localhost`, and `servername.` are rejected on both create and edit.

The add/edit form shows domain validation inline below the field instead of a browser validation popup. The message clears when corrected; API validation still applies to every caller.

The UI shows advisory messages for IPv4 destinations ending in `.1` or `.255`, without rejecting the API request or blocking Save. `.1` is often a router/gateway address; `.255` is a broadcast address for a `/24` network, and the subnet mask determines its actual role.

Each destination host/port pair must also be unique across routes: `192.168.120.40:25565` and `192.168.120.40:25566` can coexist, but another route to `192.168.120.40:25565` is rejected with HTTP **409**. An edit excludes its own row from the check, so keeping its current destination is allowed. IP literals are compared in canonical form (including IPv6 and IPv4-mapped IPv6); hostnames ignore case and a trailing dot. Hostnames are compared by name, without resolving DNS aliases. Validation and saving share a SQLite write transaction so concurrent API calls cannot bypass the check. Existing rows/schema are not modified automatically.

Proxy responses contain `id`, `domain`, `destinationHost`, `destinationPort`, `isActive`, nullable `isReachable`, and nullable `lastCheckedAt` (UTC). `isActive` is the stored configuration flag; `isReachable` is a cached TCP connection check of the configured host/port, initially null while checking. Checks run every 10 seconds with a 2-second timeout. Health is held in memory, never written to the database. The UI labels a responding backend **Active**; it polls this list every 5 seconds.

Each proxy response also includes integer `liveConnectionCount`: the current number of named forwarding sessions for that proxy, or `0` when none are open. It uses the same in-memory sessions as `/api/v1/proxies/{id}/connections`, excluding pending logins, status pings, and failed backends. Counts include separate sessions with the same player name and reset when the app restarts. The configured-proxy table and mobile cards show this number, updated by the existing 5-second list refresh without an extra request per row. These are open TCP sessions, not confirmed Minecraft joins.

Successful creation makes the route available immediately. A successful edit removes the old domain mapping, installs the new settings, and cancels only that route's existing connections. Players on it must reconnect; unrelated sessions and the shared listener keep running. Validation/uniqueness failures do not reset the running route. Routes remain enabled when a backend becomes unreachable and recover without a manual toggle. Saved routes load automatically on app startup.

DELETE `/api/v1/proxies/{id}` removes the saved route, its domain mapping, health checks, and existing connections. Other routes and the shared listener stay running. The deleted domain and destination can be reused immediately.

Save proxy settings:

```json
{ "listenPort": 25565 }
```

The runtime tries to bind the new port before releasing the previous accepting socket. Existing forwarding streams remain connected. A bind failure still saves the requested setting, reports `listenerError`, and retains the previous `listeningPort` (or null if none). Check these response fields even on HTTP 200; correct the port and save again to retry.

Full Minecraft status details and multi-user management are not implemented. Dashboard authentication, including optional 2FA, controls management access; Minecraft player authentication remains the backend server's responsibility.

### Public dashboard settings

`PUT /api/v1/settings/app` accepts `publicDashboardEnabled`, `showPublicDomains`, optional `publicDashboardTitle`, and optional `publicDashboardMessage`. The title defaults to `Welcome to ProxyCraft` (1–120 characters), and the optional paragraph defaults to `The Minecraft proxy that just works.` (0–500 characters). Titles must contain non-whitespace text. Both fields are trimmed before saving and rendered as text. Send `publicDashboardMessage: ""` to clear the paragraph; whitespace-only messages also become empty, and no paragraph is rendered. An empty saved message remains empty after restart. Omitted/null text fields retain their saved values, including when hiding the editors. Invalid supplied text returns 400 without changing settings. Existing saved messages become the paragraph; missing titles use the default without rewriting the settings file at startup.

Turning public access off forces `showPublicDomains` to false, even if an external caller sends true. Re-enabling does not automatically re-enable domain visibility. Settings remain in `settings.json`; no database schema change is needed.

`GET /api/v1/public/dashboard` returns `{ "domains": ["play.example.com"], "message": "The Minecraft proxy that just works.", "title": "Welcome to ProxyCraft" }` when both visibility options are on. Hidden names produce an empty array; disabling public access returns 404. MVC `/dashboard` is an admin preview using the normal management access rules; anonymous visitors still use `/` when enabled. Public domain buttons copy names without making management API calls; their confirmation clears after two seconds.
