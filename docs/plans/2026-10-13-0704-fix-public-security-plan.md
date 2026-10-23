---
title: Public Hawk Security Fixes - Plan
type: fix
date: 2026-10-13
artifact_contract: ce-unified-plan/v1
product_contract_source: ce-plan-bootstrap
execution: code
status: completed
---

# Public Hawk Security Fixes - Plan

## Goal Capsule

Restore login-session revocation, reduce abuse of the public login and email forms, prevent anonymous disclosure of diagnostics or captured test emails, and require a protected POST for manual monitor execution in a single-owner Hawk installation.
The user authorized implementation with “do it,” including the previously explained monitor GET fix, then authorized “deploy to prod1.” Implementation, verification, and production deployment are complete.

## Product Contract

### Deployment baseline

The production configuration in the adjacent `DualConsultInfrastructure` repository uses separate apps and PostgreSQL VMs and includes no mock server (`../DualConsultInfrastructure/apps/hawk/app.compose.yaml` and `db.compose.yaml`). Hawk binds its app port to the private apps-VM address; PostgreSQL uses the private database VM and firewall restrictions.
Caddy blocks registration paths for non-admin source IPs (`../DualConsultInfrastructure/apps/hawk/site.caddy`).
These files support the existing deployment controls; the live installation was verified during the production deployment below.
The SQL Server and PostgreSQL Compose files in this repository are separate local/test stacks, not evidence of production port exposure.

### Requirements

**Public account endpoints**

- R1. Rate-limit password-login and password-reset/confirmation-resend submissions, and count failed password attempts toward a temporary account lockout.
- R2. Keep legitimate login and account recovery usable and avoid revealing whether an email belongs to an account through cooldown responses.

**Error disclosure**

- R3. Production errors show a generic message and request ID; exception details remain in server logs. Anonymous email-send failures also receive safe generic responses. Malformed emailed tokens receive a normal validation error.

**Mock email capture**

- R4. The mock server exposes `/emails` capture, listing, and reset only when explicitly enabled in Development or Testing; Production exposes none of them even if capture is enabled.
- R5. Local mock ports bind to loopback, E2E mocks remain internal to their Docker network, and captured payloads omit bearer credentials.

**Login-session revocation**

- R6. Existing login cookies are rejected when their account is deleted or its security stamp changes, including after password change/reset. Cookie validation also refreshes role claims so removed Admin privileges cannot persist through sliding renewal. Validate on the first authenticated request after the one-minute validation interval has elapsed; unchanged accounts remain signed in.

**Manual monitor execution**

- R7. GET and HEAD requests never execute a monitor. Manual execution requires an antiforgery-protected POST and redirects to passive saved run diagnostics.

### Scope decisions

Retain the existing Caddy registration restriction and PostgreSQL production deployment.
Hardcoded SQL credentials and forwarded-host changes were explicitly excluded by the user.
The user selected the GET monitor-test fix when authorizing implementation.
No new MFA setup, database migrations, provider changes, or general security refactor is included.

## Planning Contract

- KTD1. Use ASP.NET Core's built-in rate limiter for client request limits, Identity's existing account lockout, and a bounded expiring per-account email cooldown. No Redis or new persistent model is needed for the current single app instance. Policies must use the client address supplied through Caddy and must not trust a new raw request-header key.
- KTD2. Start with configurable defaults: login submissions 10 per client per minute; Identity lockout after 5 failed attempts for 5 minutes; email submissions 5 per client per 15 minutes and one send per account per minute. Reject excess client requests with HTTP 429 and `Retry-After`; do not queue them. Account email cooldowns return the same generic result as an unknown account. Limits apply only to the relevant POST actions.
- KTD3. Keep exception capture and server logging, but never send exception type, message, inner exceptions, or stack traces in public Production HTML, inline scripts, or clipboard data. The admin-facing error page is also generic in Production; investigating by request ID uses the logs.
- KTD4. Add a disabled-by-default mock capture switch; permit activation only in Development or Testing. Update every local/E2E mock service explicitly, including the current E2E mocks that run as Production. The existing email `body` contract remains available to enabled tests.
- KTD5. Preserve Identity's registered cookie events and await its existing security-stamp validator before Hawk's validation logging; customize only the needed logging/redirect callbacks. Configure a one-minute `SecurityStampValidatorOptions.ValidationInterval` for R6, rather than implying revocation is instantaneous. The validator rebuilds the principal on successful periodic validation and rejects deleted/stamp-invalid accounts. (session-settled: user-directed — restore Identity validation over keeping logging-only validation: password resets must revoke existing login sessions.)

## Implementation Units

### U4. Restore Identity cookie validation (execute first)

Implement R6 using KTD5 so a copied login cookie cannot retain access after password reset, account deletion, or an Admin-role removal.
Touch `Hawk.Web/Program.cs` and focused HTTP/integration tests in `Hawk.Tests`; preserve existing cookie flags and redirect logging.
Use an independently retained login cookie to verify unchanged accounts stay signed in, password change/reset and deletion invalidate old cookies at the next due validation, and a removed Admin claim no longer authorizes Admin or Hangfire after principal refresh.
Advance the framework test clock or shorten the validation interval in tests rather than sleeping for a minute.
Verify the rejection path does not log a rejected principal as successfully validated and that sliding expiration cannot renew stale access once validation is due.

### U1. Limit login and account-email abuse

Implement R1-R2 using KTD1-KTD2 so automated requests cannot perform unlimited password work or send unlimited account emails.
Touch `Hawk.Web/Program.cs`, `Hawk.Web/Infrastructure/IdentitySetup.cs`, `Hawk.Web/Areas/Identity/Pages/Account/Login.cshtml.cs`, `ForgotPassword.cshtml.cs`, `ResendEmailConfirmation.cshtml.cs`, and corresponding configuration/docs; add a small shared cooldown service if required.
Verify failed-attempt lockout, expiry and successful recovery; client throttling on real POST actions; same-account email suppression across client addresses; distinct-account independence; generic results for unknown accounts, cooldowns, and sender failures; bounded cooldown storage; and unaffected GET forms, health checks, and scheduled monitor alerts.

### U2. Keep Production diagnostics private

Implement R3 using KTD3 so malformed public requests do not expose implementation details.
Touch `Hawk.Web/Pages/Error.cshtml(.cs)`, token-decoding handlers in `Hawk.Web/Areas/Identity/Pages/Account/ResetPassword.cshtml.cs` and `ConfirmEmail.cshtml.cs`, and the anonymous email-send exception handlers in `ForgotPassword.cshtml.cs` and `ResendEmailConfirmation.cshtml.cs`.
Verify malformed Base64Url values return controlled errors, valid tokens still work, and a deliberate Production exception exposes only generic text/request ID while the matching server log contains diagnostics.
Check rendered scripts and copy controls as well as visible HTML.
Verify provider failures reveal neither `ex.Message` nor account existence through different result pages or redirect behavior, while logging the full exception server-side.

### U3. Make mock email capture explicitly test-only

Implement R4-R5 using KTD4 so an accidentally launched production-mode mock cannot expose account emails.
Touch `Hawk.MockServer/Program.cs`, `docker-compose.yml`, `docker-compose.postgres.yml`, both E2E Compose files, and mock/test documentation.
Bind the local mock publication to `127.0.0.1:17801`; keep the mock listening inside its container for app-to-mock traffic.
Verify every capture endpoint returns 404 in Production (including with the switch enabled), disabled Development/Testing also returns 404, and explicitly enabled Testing still supports capture/list/reset without recording Authorization values.
Run existing alert/email-confirmation E2E scenarios against the updated mocks to catch environment-gating regressions.
No production database-network changes are needed for this unit.

### U5. Require protected POSTs for manual execution

Implement R7 by redirecting the legacy GET Test route to monitor details, reusing the existing `OnPostRunNowAsync` queue-and-redirect handler, and providing a protected **Run again** form on saved run diagnostics.
Verify GET/HEAD create no request/run/alert, missing antiforgery tokens return 400, a valid POST queues one run, and refreshing results does not execute another check.

## Verification Contract

Use targeted HTTP/integration tests for cookie revocation, role refresh, login lockout, limiter, error-response, and environment behavior rather than source-text assertions.
Run relevant `Hawk.Tests` checks with `dotnet test`; run the affected Playwright scenarios against the PostgreSQL E2E stack, with a focused compatibility check for the SQL Server test configuration.
Do not run dev-server commands. Release image builds and the app-only production update were authorized by the subsequent “deploy to prod1” request.

For implementation review, compare the intended production configuration with `../DualConsultInfrastructure/apps/hawk/app.compose.yaml`, `db.compose.yaml`, and `site.caddy`: PostgreSQL remains private, the mock remains absent, and non-admin registration remains blocked.

## Definition of Done

R1-R7 are covered by behavioral checks; enabled E2E capture still works; production errors and disabled mock endpoints disclose no diagnostics, emails, or bearer values.
The existing production registration and database controls remain effective.
Implementation receives a separate security review before release.

## Explained Finding: Monitor Test GET

Before this fix, `Hawk.Web/Pages/Monitors/Test.cshtml.cs` executed a stored monitor from its GET handler.
If the signed-in owner follows an attacker-supplied top-level link to `/monitors/test/<known-monitor-guid>`, the browser sends its SameSite=Lax login cookie and Hawk executes that monitor.
The execution can send the stored POST body and authentication headers, write history, and trigger an alert, even when the monitor is paused.
The attacker needs the monitor GUID and cannot read the cross-origin response or choose a new body through this link.
Restricted registration removes the easy way to learn IDs, so this is a conditional, lower practical risk for this deployment.
The implemented fix keeps GET passive and moves execution to an antiforgery-protected POST.
Replace the initial Test action and Run again link with POST forms carrying Razor's antiforgery token.
After execution, redirect to an existing run's diagnostics URL whose GET handler only reads persisted results; refreshing or opening that URL must not execute another check.
The existing `OnPostRunNowAsync` handler in `Hawk.Web/Pages/Monitors/Details.cshtml.cs` already queues a run and redirects to passive diagnostics, so reusing that flow is the simplest option if queued execution is acceptable.
The old GET Test URL must also stop executing, for example by redirecting to the monitor details page.
Verification would assert that GET and HEAD create no outbound request/run/alert, a POST without a valid antiforgery token returns 400 without execution, a valid form POST starts one run, and refreshing the results creates none.

## Completion Evidence

- All 108 .NET tests passed with `HAWK_REQUIRE_POSTGRES_TESTS=1`, including real PostgreSQL integration tests and new security HTTP tests.
- All 10 affected browser tests passed against an isolated PostgreSQL stack using existing compiled artifacts, covering login, account confirmation, manual runs, scheduling, and alert capture.
- All four Compose configurations validated. Existing production infrastructure was not changed.
- A separate security reviewer found no actionable issues in the final implementation.
- Existing unrelated edits were preserved.

## Production Deployment — 2026-10-13

- User authorization: “deploy to prod1.” Version: **0.9.30**; source commit: `b70eb85abefd253990e0390265a701b57d286c04`.
- Image: `registry.dualconsult.net/dualconsult/hawk@sha256:c855af1e905e95588c21412d3754bb0fa527e63453610283011c6838a0ce7266`.
- Build context was exported from the committed source, excluding unrelated local edits and untracked plans. The release-only asset stage used Bun 1.3.11 and a frozen lockfile imported from the existing package lock; the reviewer confirmed all dependency versions/integrity hashes and final runtime settings were preserved.
- Release verification: 108 tests passed in Release mode with required real PostgreSQL checks; 10 headed browser tests passed against the exact image with a read-only root filesystem and dropped capabilities.
- Deployment: `scripts/deploy-app.sh hawk app` in the infrastructure repository; release pin committed as `70a11e0`. Local/live Compose and secret-file hashes matched before upload. Only the apps VM was updated.
- Live checks: container digest/revision/version matched; service active with zero restarts, UID 1654, and read-only root filesystem; public `/live` and internal `/ready` healthy; public `/ready` and `/emails` 404; anonymous monitor/admin access required authentication; non-admin registration 403; public error HTML exposed only generic information and request ID.
- A separate reviewer independently verified the live release and found no issues.
- Previous image for rollback: `registry.dualconsult.net/dualconsult/hawk@sha256:357f2439579fbfec594d4fc4d3e3ccf518429db3a9c3855bd6146a1e8962029c`. Restore that pin and rerun the same app-only deployment command.

## Sources

- `docs/plans/2026-10-12-migrate-hawk-to-app-server-plan.md`
- `Hawk.Web/Program.cs`, account page handlers, and `Hawk.MockServer/Program.cs`
- [ASP.NET Core rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0)
- [ASP.NET Core antiforgery guidance](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0)
