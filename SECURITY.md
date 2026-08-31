# Security policy

PriceSentinel handles OAuth credentials and can submit real equity orders after
LIVE is explicitly armed. Please report security problems privately and avoid any
testing that could affect an account you do not own.

## Supported versions

The latest published release and the latest code on the `main` branch are
actively supported. Security fixes can reach `main` before the next packaged
release. Older releases, commits, local forks, and unofficial binaries may not
contain current safety or security fixes.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability involving authentication,
credential storage, Robinhood communication, order review or placement,
idempotency, LIVE arming, cancellation, or another safety control.

Use GitHub's private vulnerability-reporting feature for this repository when it
is available. Otherwise, contact the repository owner privately through the
contact method on their GitHub profile. Please allow time to investigate and
coordinate a fix before public disclosure.

Include only the minimum information needed to reproduce the issue:

- the affected commit or version;
- Windows and .NET versions;
- a concise impact description and sanitized reproduction steps;
- whether the issue was observed in Replay, Paper Trader, disarmed LIVE, or armed
  LIVE; and
- sanitized exception text or screenshots when they are necessary.

Never include Robinhood passwords, access or refresh tokens, dynamic client
secrets, account numbers, order IDs, raw OAuth callback URLs, unredacted activity
logs, journal databases, or other personal trading data. PriceSentinel never needs
a Robinhood password to diagnose a report.

Do not deliberately submit a real order to prove a vulnerability. Prefer Replay
or Paper Trader, and stop immediately if a test unexpectedly reaches armed LIVE.

Strategy performance, an unprofitable trade, or disagreement with a deterministic
signal is not by itself a security vulnerability. A way to bypass an explicit
risk, authorization, idempotency, or execution guard is in scope.
