# Validation of fixed morning / DB-first update

- .NET 10 API Release compilation passed.
- Angular 18 production build passed, including strict template checking.
- Verification console passed 32 assertions using an in-memory repository and deterministic API fixtures.
- Covered both exact selection branches and midpoint rounding; ordinal unequal-strike pairing; delta-based short selection; UTC/IST parsing; forming-bar exclusion; exactly eight option downloads plus NIFTY cash on first preparation; persistence before replay; frozen selections; zero API calls on a complete cached day; database-only replay; next-Open entry and 15:15 EOD exit; rejecting outside-set downloads; full-contract refetch for one missing bar; saving partial responses while blocking incomplete replay.

The replay checks are functional fixtures, not a new historical profitability study. The test repository does not validate a running PostgreSQL instance. Actual Breeze session authentication, your legacy database schema/data, and Kotak order execution were not exercised because credentials and a connected database were not provided. No live orders were placed. Configured costs and lot sizes remain assumptions until matched to your trading account/date.

First-stored selection update: default lots 7/size 65; source-fallback regression rejects the09:20 Open in favor of09:15. Historical fixture replay independently matched 2,576 trades across five variants on 350 sessions. See ../lot7-study/REPORT.md.
