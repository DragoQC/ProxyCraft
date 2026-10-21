# Roadmap

## Current foundation — implemented

- [x] Single root application project targeting .NET 10.
- [x] Replace Blazor with MVC and remove the Blazor skill.
- [x] Lean Tailwind UI with home, App Settings, Proxy Settings, and configured proxies.
- [x] Two-column desktop masonry-style App Settings with intact cards and a single column on mobile; retain JSON settings storage.
- [x] Mobile proxy cards, icon-only mobile branding/theme picker, and automatic Rainbow effects without a manual toggle.
- [x] Home CPU/RAM/disk capacity and usage cards, with Linux cgroup v1/v2 support and a shared metrics API.
- [x] Node-free Tailwind builds through dotnet/MSBuild; no npm modules or separate CSS watcher.
- [x] Debian 13 curl-to-bash installer downloading the public DragoQC/ProxyCraft repository, and Docker/Compose deployment with persistent data and cookie keys.
- [x] Shared HTTP API for UI and external callers.
- [x] SQLite with EF Core 10.0.12 and locked dependencies.
- [x] `User` and `Proxy` inherit `BaseModel` with creation/modification timestamps and active state.
- [x] Automatic initial schema creation on launch, preserving existing data on restart.
- [x] Recreate a deleted database on the next launch; reset account setup for the fresh database.
- [x] Start without login, then optionally set up a main account in App Settings and require sign-in.
- [x] Hashed passwords, cookie sessions, CSRF protection, session validation, and login rate limiting.
- [x] Optional authenticator-app 2FA with encrypted user fields, code replay prevention, recovery codes, and confirmed setup/removal.
- [x] Opt-in anonymous welcome dashboard with an editable plain-text title and optional paragraph, dependent domain visibility, and click-to-copy names; management information remains protected.
- [x] Admin Dashboard preview link and persistent navigation icons with evenly spaced mobile icon-only labels.
- [x] Key-authenticated SignalR domain-add/remove events, including renames and revoked-key rejection, documented in the custom API reference.
- [x] Human-readable contracts, explicit conditional blocks, consistent source formatting, and expanded Razor/styles without a UI redesign.
- [x] Search proxies at the top left, add (+) at the top right, and list saved configurations.
- [x] Save proxy listener settings with port 25565 as the default.
- [x] Integration checks for API behavior, auth, persistence, and repeated database recreation.
- [x] App Settings API-key generation/rotation, singleton hashed storage, and key authentication for external API requests.
- [x] Custom C#/Razor API reference linked from App Settings, with inputs, outputs, validation rules, response codes, and JSON examples; no documentation packages.
- [x] Require a dot in route domains; show non-blocking IPv4 .1/.255 hints in add/edit forms.

## Minecraft networking — implemented

- [x] Host-owned TCP listener on default port 25565.
- [x] Bounded Java Edition handshake parsing, including fragmented packets and timeouts.
- [x] Select configured domain routes and connect to local/LAN/external destinations.
- [x] Relay buffered handshake and bidirectional traffic with cancellation and graceful shutdown.
- [x] Handle malformed clients, unknown domains, and unavailable backends explicitly.
- [x] Per-row editing through the shared API; save immediately replaces only that route and resets its connections.
- [x] Pen and trash icons on each row; deletion removes the saved route and closes only its connections.
- [x] Click a domain to copy it, with accessible feedback that clears after two seconds.
- [x] Save and display the latest 20 player login attempts per proxy, exclude status pings, and cascade history deletion with the proxy.
- [x] Monitor/activity live-connections view with in-memory session tracking, independent same-name sessions, and cleanup on disconnect/reset/restart; preserve saved history.
- [x] Numeric live connection counts in proxy table rows/mobile cards and the shared list API, refreshed without replacing row controls.
- [x] Scrollable live/history lists with stationary dialog controls, preserved reading position, 100-session forwarding checks, and desktop/mobile browser coverage.
- [x] Cached TCP reachability checks and live status in the list/API, independent of stored enablement.
- [x] Test actual parsing, forwarding, route isolation, backend health/recovery, listener changes, and cancellation.

## Later

- [ ] Full Minecraft status responses (TCP reachability is implemented).
- [ ] Deliberate schema evolution strategy, with migration approval before any migration work.
