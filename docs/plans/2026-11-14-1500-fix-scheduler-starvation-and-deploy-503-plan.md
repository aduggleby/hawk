# Fix scheduler thread-pool starvation and the 503s during deploys (0.16.5)

Found with the 0.16.4 debugging aids (slow SQL warnings, request ids), 2026-11-14.

## Problems

1. **Pages and checks stall for 1-4 s every 30 s.** Slow SQL warnings showed trivial queries (primary-key lookups
   on 48 rows) taking 0.7-4 s in bursts, with an idle database. `dotnet-counters` against the live container:
   thread-pool queue 7-9 items with 4 threads and ~0 CPU; the stalls ended when the pool injected threads.
   `dotnet-stack` during a burst: every busy pool thread was in `MonitorScheduler.TickAsync` ->
   `PostgreSqlConnection.AcquireLock` -> `Thread.Sleep` (Hangfire.PostgreSql polls for the lock).
   Cause: **18 self-scheduling tick chains** in Hangfire (one more per app start, since `EnsureStartedAsync`
   enqueues a tick while the previous run's scheduled tick survives). The lock was meant to collapse duplicates,
   but a tick waits up to 60 s for it (longer than the 30 s tick), so every waiter eventually runs and schedules
   its own next tick. Each waiting tick blocks a pool thread.
2. **503 "no upstreams available" for ~30 s after a deploy with an edge stage.** The Caddy reload ran its first
   active `/ready` check while the app was still starting, marked the upstream down, and only re-checked 30 s later.
3. **Stale monitoring ignore entry** (`monitoring/ignore/hawk.txt`, infra repo): aborted `/ready` probes no longer
   log as ERR since 0.16.1.

## Fixes

1. Scheduler (`MonitorScheduler.TickAsync`):
   - Take the tick lock **without waiting** (`TimeSpan.Zero`); busy = exit without scheduling (the holder schedules).
     No pool thread sleeps.
   - **Collapse duplicate chains**: under the lock, read `lastTickAt` from a Hangfire hash (`hawk:scheduler`).
     If the previous tick ran less than 3/4 of a tick interval ago, this tick is a duplicate chain: exit without
     scheduling. Otherwise record `now` and continue. A single chain always has gaps of at least one interval,
     so it is never dropped; any second chain dies within one interval. The existing 18 chains collapse on deploy.
   - Review change: a duplicate tick exits at once (no work), so it cannot hold the lock when the surviving
     chain's next tick arrives (which could end both chains).
   - **Watchdog** (review finding): minutely recurring `hawk-scheduler-watchdog` enqueues a tick when `lastTickAt`
     is missing or older than 3 intervals + 60 s. Covers a tick that throws after recording itself, a transient lock
     error counted as busy, and a lock orphaned by a crash; before, only an app start restarted a dead chain.
   - Pure rules `IsDuplicateTick` and `IsChainStale` with unit tests; Postgres tests for back-to-back ticks, a busy
     lock (returns within 1 s, no work) and the watchdog.
2. Edge (`apps/hawk/site.caddy`, infra repo): `health_interval 5s`, `health_fails 2` (one slow probe does not take
   Hawk out), `lb_try_duration 15s` / `lb_try_interval 500ms` (requests wait for the upstream during a restart
   instead of failing at once).
3. Remove the stale ignore entry.

Out of scope (noted): other distributed locks (alert, heartbeat, run buckets) also wait with `Thread.Sleep`, but per
monitor and rarely contended; revisit if slow SQL warnings show bursts again.

## Verification

- Unit + Postgres tests (`HAWK_REQUIRE_POSTGRES_TESTS=1`), `ando run`, independent review.
- After deploy: scheduled `TickAsync` jobs in `hangfire.job` drop to 1; no Slow SQL bursts for 10 min;
  `dotnet-counters` thread-pool queue stays ~0; a deploy with an edge stage has no 503 in the access log.

## Rollback

Revert the infra pin to 0.16.4 (`sha256:dd7ea5ad…`), then delete the `hawk-scheduler-watchdog` recurring job in the
Hangfire dashboard (0.16.4 has no `WatchdogAsync`). No schema change; the `hawk:scheduler` hash is ignored by 0.16.4. Until the job is deleted (or while old and new
containers overlap during a deploy) the old image logs one failed watchdog job per minute; harmless.
