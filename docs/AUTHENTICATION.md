# Sign-in, two factors, and public visibility

Fresh installations retain the existing behavior: sign-in is off, and anyone who can reach the dashboard can manage it. Enable authentication before exposing a read-only welcome page to visitors.

## Authenticator setup

1. Enable authentication in App Settings, then sign in with the main account.
2. Under **Two-factor authentication**, enter your current password and choose **Set up authenticator**.
3. Add a time-based account in your authenticator app. Enter the displayed secret manually, or use **Open in an authenticator app** on a supported device. Setup expires after ten minutes.
4. Enter the six-digit code and confirm. Two-factor authentication becomes active only after verification.
5. Save the eight recovery codes immediately. They are shown once. Existing sessions are invalidated; sign in again with your password and a new authenticator code, or one unused recovery code.

The login form's authenticator/recovery field is optional for accounts without 2FA and required when it is enabled. Codes are time-based: SHA-1, six digits, 30-second periods, with one neighboring step allowed for clock differences. An accepted step cannot be reused. Recovery codes are random, single-use alternatives to the authenticator; they still require the account password.

Disabling 2FA requires the current password and a fresh authenticator code or unused recovery code. It invalidates existing sessions. The app reports how many recovery codes remain; it cannot display old codes again. Account setup after disabling and re-enabling dashboard authentication resets the old factor along with the account's credentials.

## Storage and validation

The user approved optional fields on the existing `users` table:

| Field | Stored value |
| --- | --- |
| `TwoFactorSecret` | Nullable authenticator secret encrypted by ASP.NET Core Data Protection. Null means 2FA is off. |
| `RecoveryCodeHashes` | Nullable JSON array of SHA-256 hashes of random recovery codes. |
| `LastTotpStep` | Nullable last accepted time-step counter, preventing replay. |

Startup checks `PRAGMA table_info` and adds only these missing nullable columns to an older users table. Existing IDs, credentials, routes, and history stay intact. Fresh databases include the columns through EF Core. There is no additional table or EF migration command, and `BaseModel` retains its original three fields.

Code consumption occurs under an immediate SQLite write transaction. Concurrent attempts cannot consume one recovery code twice. Enrollment tokens are protected, expire after ten minutes, and are bound to the account's current security stamp. Enabling or disabling 2FA changes the stamp, invalidating earlier sessions and enrollment tokens. Login and enrollment mutations share the existing ten-attempts-per-minute IP rate limiter. Responses containing secrets or recovery codes use `no-store`.

Preserve the Data Protection key ring with the database when moving or updating an installation. The supplied native/Docker deployments already persist these keys. Authenticator setup, status, and removal require an authenticated account cookie and normal browser CSRF protection; an API key alone cannot manage a user's factor. Management API keys retain their existing administrative permissions, so keep them private.

TOTP follows [RFC 6238](https://datatracker.ietf.org/doc/html/rfc6238). Password hashing, cookie validation, CSRF protection, and login rate limiting retain their existing ASP.NET Core implementations.

## Public welcome page

App Settings contains two independent settings, both off by default:

- **Allow the welcome dashboard without signing in:** while authentication is on, an anonymous visitor to `/` sees **Welcome to ProxyCraft** and a sign-in link. Turning it off sends visitors to sign-in.
- **Show domain names on the public dashboard:** optionally lists the configured active domain names. It has no effect while the public dashboard is disabled.

The public page has no management navigation, destination IP/port, player list, history, or resource metrics. Signed-in users retain the usual management dashboard. Disabling sign-in still grants the original open management access; the public-page setting does not turn that mode into restricted access.

`GET /api/v1/public/dashboard` is the narrow anonymous API for this page. It returns `{ "domains": [] }` when names are hidden, permitted names when visible, and 404 when the public page is disabled. It intentionally requires no API key. Management endpoints and the domain-event hub retain their authorization rules.

`tests/features_test.py` checks schema upgrade/preservation, visibility permissions, authenticator enrollment, encrypted storage, code replay, concurrent recovery use, session invalidation, and restart persistence. `tests/account_browser_test.py` exercises the actual setup/login/removal and public-settings controls in Firefox.
