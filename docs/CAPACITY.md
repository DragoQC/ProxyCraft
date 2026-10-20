# Connection capacity and display

The current proxy passed a local test with **100 concurrent named forwarding sessions**, including a simultaneous login burst, verified bidirectional traffic, API polling, and complete disconnect cleanup. No backend changes were needed for that target. This is a measured workload on the development host; it does not establish a universal player limit or simulate a full modded Minecraft session.

## Repeat the capacity check

After `dotnet build --no-restore`, run:

```bash
python3 tests/capacity_test.py
```

The standard-library test starts the real app and a local echo backend on unused ports, with temporary SQLite/settings. It launches 100 login attempts concurrently, holds all 100 sockets open, checks distinct session identities, and relays traffic on every connection while polling live/history/metrics APIs. History must remain at 20. Closing every connection must clear the live list and retain the saved attempts. No production routes, players, or database are used.

One local Linux run on October 20, 2026 produced:

| Measurement | Result |
| --- | --- |
| Established forwarding sessions | 100 |
| Time to open the simultaneous burst | 0.32 seconds |
| Verified traffic across both directions | 80.03 MiB |
| Slowest of five live-list requests during traffic | 4.61 ms |
| Proxy process resident memory before the burst | About 143 MiB |
| Peak proxy process resident memory | About 165 MiB |
| Peak proxy process threads | 46 |
| Peak open file descriptors | 532 |
| Saved history | Exactly 20 entries for the proxy |

These resource samples include the .NET process and its dependencies. They do not include the Python test clients/backend, kernel socket buffers, or the actual Minecraft server. The short test covers connection handling and correctness; it is not a long-duration soak test or a throughput guarantee. Repeating it prints measurements for the current host.

## Why 100 connections fit the design

- Forwarding uses asynchronous network I/O. A connection has two sockets and two copy operations; an idle session does not reserve a dedicated worker thread.
- Live records contain a connection ID, proxy ID, supplied player name, domain, and UTC time. They stay in memory and are removed on closure, failure, route reset, and shutdown.
- SQLite is written on the initial named attempt, rather than on each packet or live-view refresh. Inserts and pruning share one transaction; a burst cannot leave more than 20 committed attempts per proxy.
- Live polling takes a short snapshot under the existing route lock, then serializes it outside the lock. A viewer requests one proxy every two seconds while the dialog is open and the tab is visible. Forwarding does not depend on an open browser.

For an installation serving 100 players, allow for at least 200 game sockets plus HTTP, SQLite, and runtime files in the service's effective file-descriptor limit. Monitor the existing CPU/RAM cards and network usage under representative gameplay. The backend Minecraft servers still need their own capacity for those players; forwarding tests do not measure their tick rate, world generation, or modpack behavior. No service limits or deployment settings are changed by these checks.

## Lists and mobile layout

The live API returns all current named sessions; 100 is a tested target, not a new hard cap. The saved-events API returns at most 20 attempts per proxy. Both views keep the heading, count, and Close button above a keyboard-focusable scrolling list, constrained to the current viewport. Long names/domains wrap. The last entry remains reachable without scrolling the controls away.

Unchanged polls skip rendering. A changed snapshot is applied in one document-fragment update. When the reader has scrolled, the same visible entry retains its offset if it still exists; if it was removed, the previous scroll position is clamped to the remaining list. New dialogs start at the top. Rendering 100 entries does not require pagination or a new frontend dependency.

Configured proxies use a desktop table and separate cards below 768px. Each mobile card shows the domain, labeled destination, backend status, and 44px live/history/edit/delete controls. The header uses the logo and theme icons below 640px, with accessible labels. Rainbow effects run automatically when selected, respect reduced motion, and pause in hidden tabs.

With Firefox installed, run the browser check after a Debug build:

```bash
python3 tests/browser_test.py
```

It uses a fresh Firefox profile and temporary app data, opens 100 real forwarded sessions, and checks the 100-entry live list and 20-entry history at 1440px, 768px, 390px, and 320px in all three themes. It also checks scroll anchoring during joins/disconnects, keyboard scrolling, mobile actions, the retired effects preference, and device reduced-motion changes. Screenshots are written to the temporary directory printed by the test. No browser packages, Node, or external test service are required.
