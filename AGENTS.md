# ProxyCraft agent instructions

## Communication and scope

- Always respond in English. Be concise, practical, and direct; explain the reasoning that matters.
- Inspect relevant files and existing patterns first. Make the smallest correct change without unrelated refactoring or renaming.
- Report important files changed, verification, and material limitations. Ask only when genuinely ambiguous or when separate approval is required; existing task authorization remains valid.
- Do not add dependencies without approval. The user authorized Tailwind and EF Core SQLite and explicitly requested removal of the documentation packages.
- Do not create, modify, apply, or suggest database migrations without approval. The user explicitly approved automatic initial SQLite schema creation on application launch, and recreation after deleting the database. This does not authorize later migrations or automatic alteration of existing schemas.
- Do not modify infrastructure, CI, Docker, or deployment files unless asked. Do not perform destructive or irreversible actions without explicit approval.
- The user explicitly requested `install.sh`, Docker image/build files, and Debian 13 deployment documentation. Keep the native installer limited to Debian 13/systemd; preserve data on updates. Docker runs as a non-root user and uses a persistent data volume. Do not publish an image or invent a public registry URL. The user explicitly authorized creating and publishing `https://github.com/DragoQC/ProxyCraft`, the initial commit/push, and a curl-to-bash installer that downloads this repository by default. Keep `install.sh` limited to that repository; no local-source or repository-selection options are needed.

## Stack and organization

- The application project is `ProxyCraft.csproj` at the repository root. Keep it there.
- ASP.NET Core MVC on `net10.0`, with Razor `.cshtml` views. The user replaced Blazor with MVC and requested removal of the Blazor skill. Do not reintroduce either.
- Tailwind CSS is pinned to **4.3.3** and compiled with its official standalone executable through `Build/Tailwind.targets`. Keep the project free of Node/npm, npm manifests, `node_modules`, and a separate CSS watcher.
- Rebuild styles with `dotnet build -t:BuildCss --no-restore`. This explicit, one-shot target downloads the matching SDK-host compiler on first use into ignored `.tools/`, verifies the pinned SHA-256 checksum, and runs it. Normal build/run uses the included CSS without downloading or starting Tailwind.
- Entity Framework Core SQLite is pinned to **10.0.12**. Inspect `ProxyCraft.csproj`, `packages.lock.json`, and restored assets before using package APIs.
- The custom API reference at `/settings/api` uses Razor and the shared framework's MVC API metadata. `Api/Documentation/` builds its view models from controller routes, response annotations, and DTO validation. Keep it informational; do not reintroduce documentation packages or a request tester.
- Keep the app small: one application project, no frontend framework, generic repository, mediator, or extra layers without a concrete need.
- Minecraft listener and backend ports default to **25565**. Dashboard HTTP/HTTPS ports are separate.
- `MinecraftProxyService` owns the Java Edition TCP listener and domain routes independently of HTTP/browser lifetimes. Route saves replace only that route and disconnect its sessions; unrelated routes stay connected.
- Reachability is an in-memory TCP-port check every 10 seconds with a 2-second timeout. Keep it separate from the persisted `IsActive` flag; do not claim it is a full Minecraft status response or ICMP ping.
- Home resource cards read `/api/v1/system/metrics`. Keep CPU/RAM measurements scoped to the Linux guest/container with visible cgroup v1/v2 limits; never substitute process-only or host-wide usage for constrained-container usage. Disk measures the filesystem holding app data. A background service samples every 2 seconds; the UI polls every 5 seconds. Missing readings stay null.

## API boundary

- MVC views use the management HTTP API through `wwwroot/js/app.js`. External clients call the same `/api/v1` endpoints.
- `Controllers/` renders pages; `Views/` handles presentation; `Api/Controllers/` handles HTTP contracts; `Api/Contracts/` contains explicit request/response DTOs; `Services/` owns application operations; `Data/` owns EF persistence; `Models/` contains stored entities; `Security/` handles optional authentication.
- Keep application behavior out of views and API controllers. API and UI operations share authoritative validation and permission checks. Never duplicate management logic in a page controller.
- Keep `Program.cs` focused on registration, startup initialization, middleware, and endpoint mapping.
- Add only requested endpoints, fields, and behavior. Document implemented contracts in `docs/API.md`.

## Models and storage

- `User` and `Proxy` inherit `BaseModel`. The base has exactly `CreatedAt`, `ModifiedAt`, and `IsActive`; map these to `created_at`, `modified_at`, and `is_active` database columns. Entity IDs belong on derived models.
- `ProxyCraftDbContext` timestamps async saves in UTC. Reuse this shared behavior.
- App tables are `users`, `proxies`, `api_keys`, and `connection_events`. `ApiKey` and `ConnectionEvent` also inherit `BaseModel`; the API-key ID is fixed at 1 with a database check constraint. App settings live in `App_Data/settings.json`.
- The user explicitly authorized the new API-key and connection-event tables. `DatabaseInitializer` creates only those missing tables with idempotent SQL on older installations. Preserve all existing users/routes; this does not authorize general schema changes or migrations.
- The user also explicitly approved optional 2FA fields on `users`, rather than a separate table: `TwoFactorSecret`, `RecoveryCodeHashes`, and `LastTotpStep`. Startup checks existing columns and adds only missing nullable fields; do not expand this into general migrations or recreate user data.
- Record client-supplied player names from the first login packet before connecting to the backend; server-list pings do not create events. Preserve original handshake/login bytes, bound packet sizes/read timeouts, and leave later traffic opaque. Names describe attempts, never authenticated identities or confirmed joins.
- Keep at most 20 connection events per proxy, newest first. Insert/prune atomically in a SQLite write transaction. Link history by proxy ID with database cascade deletion; retain the requested domain as a historical snapshot across edits. Show history through the shared management API and the per-row history dialog.
- Live player sessions belong only in `MinecraftProxyService` memory, keyed by connection ID rather than player name. Track only named forwarding sessions after the backend opens; exclude pending logins, status pings, and failed backends. Remove sessions on closure, errors, route cancellation, and shutdown. The monitor/activity button reads `/api/v1/proxies/{id}/connections` every 2 seconds while open and visible. The existing history button keeps its saved 20-attempt list and 5-second refresh; no live-state database fields or disconnect-event schema changes are needed. Both lists scroll within the dialog while its heading/count/Close stay visible; preserve the reading position across changes.
- On launch, `DatabaseInitializer` calls `EnsureCreatedAsync`. Keep automatic initialization and the narrow approved table/column additions; never require manual database commands or apply unrelated schema changes.
- If a newly created database replaces a deleted one, clear the saved authentication-enabled setting so the main account can be set up again. Preserve existing schemas/data on ordinary restarts.
- `Storage:Directory` controls the data directory, defaulting to `App_Data`. Keep database files, local settings, and credentials out of source control.

## Authentication

- Follow the user's final correction: fresh installations work without login. Authentication is enabled in App Settings, rather than through install flags.
- Enabling auth asks for one username OR one email address and a password, saves the main account, and redirects to sign-in. Do not automatically log in after setup.
- Use the existing ASP.NET Core cookie authentication, `PasswordHasher<User>`, security-stamp validation, and CSRF protection. Never store or return plaintext passwords or expose password hashes through the API.
- When auth is enabled, protect MVC pages and API operations. A configured API key authenticates external management calls through `X-API-Key`, without cookies or CSRF. Store only its hash; show the raw key once and invalidate the old key on regeneration.
- The UI sends its CSRF token on every API call. Once a key exists, API calls require either that real token/cookie pair or a valid key. Do not exempt requests based only on a UI header, Origin, or Referer. With sign-in off, the dashboard is deliberately public; API keys cannot make that open dashboard private.
- Authenticator 2FA is optional and implemented through confirmed password-authenticated setup, a protected ten-minute enrollment token, encrypted secret, hashed one-use recovery codes, and replay-protected TOTP counters. Factor changes invalidate sessions. Factor endpoints require an account cookie, not an API key alone. Preserve atomic code consumption and existing rate limits.
- Public dashboard/domain visibility settings are opt-in JSON booleans; disabling the dashboard clears domain visibility in both UI and API. The JSON welcome title defaults to Welcome to ProxyCraft (1–120 characters); the message is the paragraph, defaulting to The Minecraft proxy that just works. (1–500 characters). Both require non-whitespace plain text and are preserved while their editors are hidden. Existing saved messages remain the paragraph; absent titles use the default without rewriting settings on startup. Anonymous visitors see only this title/paragraph and explicitly permitted, click-to-copy domain names. The protected `/dashboard` route previews the welcome view with admin navigation; it never enables anonymous access. Never expose destination addresses, metrics, history, or management controls on that page. Login-off mode retains its original open management access.
- `/api/v1/hubs/domains` is a shared-framework SignalR feed requiring an API key. Publish DomainAdded/DomainRemoved after committed route changes; renames emit removal then addition. Revalidate subscriber key hashes before delivery. The feed is live/best-effort; document snapshot reconciliation rather than claiming durable delivery.

## Explicit C# types: required

- Target zero `var` in authored C# and Razor C# blocks. Name types wherever the language permits, including locals, loops, `out` arguments, patterns, deconstruction, and lambda parameters.
- Do not introduce anonymous types, `dynamic`, or broad `object` declarations to avoid the correct named type.
- Only use `var` when the language genuinely requires it, with a short explanation. Apparent or verbose types are not an exception.
- Keep nullable reference types enabled. Use async I/O and propagate cancellation for database/network work.
- `.editorconfig` and `EnforceCodeStyleInBuild` enforce supported `IDE0008` cases. Inspect `.cshtml` blocks and forms the analyzer cannot cover; exclude generated/dependency code.

## Existing patterns and UI

- Check manifests/lockfiles and relevant shared layouts, services, models, DTOs, defaults, helpers, and validators before editing. Verify version-sensitive behavior against official documentation for the installed versions; distinguish local and external checks.
- Reuse `Views/Shared/_Layout.cshtml`, `Styles/app.css`, existing API contracts, services, model timestamps, and auth helpers.
- Ask before expanding a local request into a broader shared/global change. Do not invent a local replacement for an established shared responsibility.
- Keep a lean, responsive operating UI with no SEO, marketing content, external fonts, or decorative packages. Preserve accessible labels, semantic headings, keyboard access, visible focus, and useful loading/empty/error states.
- Tailwind v4 uses CSS-first imports and explicit view/JavaScript sources. Keep utility names complete and literal, including JavaScript state classes.
- Shared theme tokens in `Styles/app.css` support Light, Dark, and Rainbow. Use semantic colors so all pages, dialogs, errors, and generated rows follow the selected theme. Light and Dark stay restrained; the user explicitly requested decorative neon effects for Rainbow.
- `wwwroot/js/theme.js` applies browser-local preferences before paint and owns the rainbow canvas. Rainbow effects run automatically; the user requested removal of the effects toggle and its stored preference. Preserve reduced-motion support and pausing when the tab is hidden. The shared header shows only the logo and theme icons on mobile, retaining accessible names. All five admin navigation links have icons; mobile labels remain available to screen readers and the five icons are spaced evenly left to right; desktop labels stay visible with links aligned at the start. Preserve the Dashboard preview link and 44px navigation targets. Do not add theme endpoints or database fields for browser-only presentation choices.
- Configured proxy rows become cards below 768px, with labeled/wrapping destinations and 44px action targets. Reuse the same rows and handlers across layouts.
- Read the project-local [Tailwind UI skill](skills/tailwind-ui/SKILL.md) before relevant UI work.

## Verification and documents

- Rebuild CSS after view/utility changes: `dotnet build -t:BuildCss --no-restore`. Include generated `wwwroot/app.css`; do not edit it directly. The cached standalone compiler is a development tool and must stay out of source control and published output.
- Run `dotnet build --no-restore` after restore and `dotnet format style --no-restore --verify-no-changes --diagnostics IDE0008` for type-style verification.
- Run `python3 tests/smoke_test.py` after a Debug build for real TCP forwarding, isolated route edits, health recovery, database creation/recreation, API behavior, persistence, and auth flows. These tests use local test backends, temporary storage, and app startup, never a user's database.
- Run `python3 tests/features_test.py` for optional-column preservation, real SignalR WebSocket events/key rotation, public access rules, 2FA/recovery replay, concurrent consumption, and persistence. `python3 tests/account_browser_test.py` checks the new account/public-setting flows in Firefox. Keep all fixtures temporary.
- Run `python3 tests/capacity_test.py` for 100 simultaneous synthetic sessions, verified forwarding traffic/API responsiveness, bounded history, resource samples, and complete disconnect cleanup. Run `python3 tests/browser_test.py` with installed Firefox for responsive cards, 100 live entries/20 events, scroll stability, keyboard access, theme controls, and reduced motion. These also use temporary app data; results and limits are documented in `docs/CAPACITY.md`.
- For installer changes, run `bash -n install.sh` and `python3 tests/install_test.py` (simulated host tools; no real host installation). For Docker changes, build `proxycraft:local`, validate `docker compose config --quiet`, and run `python3 tests/docker_test.py` when Docker access is available. That test owns and cleans only its temporary containers and volume.
- Inspect changed pages in a browser when practical. Add meaningful tests for behavior, not implementation-mirroring or cosmetic checks.
- Keep [README.md](README.md), [architecture](docs/ARCHITECTURE.md), [API reference](docs/API.md), and [roadmap](docs/ROADMAP.md) consistent with implemented behavior.
