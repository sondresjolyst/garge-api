## Summary

Makes a deleted user safe to keep **indefinitely** for stats, so the "users over time" chart can show signups *and* churn forever — without holding personal data past its basis.

**Stat snapshots (permanent, anonymous).** New `DailyStatSnapshot` table: one immutable per-day row of platform totals. `StatsSnapshotService` backfills the full history from the earliest signup on first run, then appends only the days missing since the last snapshot — carrying totals forward and applying each day's deltas from live data. **Past rows are never rewritten**, so the history survives even after the per-user rows it was derived from are purged (post-5y). Snapshots hold only aggregate counts → anonymous → keepable forever. `GetStatsHistory` now reads the snapshots (ensuring they're current) instead of recomputing from live data each call; a daily background job keeps them accruing without a UI visit.

**Deletion scrub.** `ScrubUserPiiAsync` now also clears `PasswordHash` and `TermsAcceptedIp`. The IP is personal data with no basis to outlive the account (not part of the bokføringsloven invoice record), so removing it keeps the soft-deleted row free of recoverable PII.

## Why this answers "delete after 5 years?"
The bare snapshot rows are anonymous, so user-count history no longer depends on keeping identifiable per-user rows. Per-user rows can be purged once their 5y accounting basis lapses (separate follow-up) and the chart is unaffected.

## Tests
- `StatsSnapshotServiceTests`: backfill climbs on signup / dips on deletion; idempotent same-day re-run; existing snapshots frozen (only later days appended, survive a "purge"); no-activity no-op.
- `UserControllerTests`: deletion nulls `TermsAcceptedIp` + `PasswordHash`.
- Existing `GetStatsHistory`/stats tests still green (now via the snapshot path). 346 tests pass.

## Migration
`AddDailyStatSnapshot` — creates the table + unique index on `Date`.

## Not in this PR
The 5y hard-delete purge job for per-user rows (now safe to add thanks to the snapshots).
