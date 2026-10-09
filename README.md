# Bybit Perpetual Futures Historical Downloader

**Status: Stage 5 of 5 — Core + storage + orchestrator + screening + CLI + GUI + offline validator, all tested.**
Everything here has been compiled and tested (`dotnet test` -> 198 passing, no network in tests).

## Requirements
.NET SDK 10 (`dotnet --version` -> 10.x). Windows, macOS or Linux. The GUI uses Avalonia 12.

## Commands
```
dotnet restore
dotnet build
dotnet test
dotnet run --project src/BybitDownloader.Gui   # desktop GUI
```
(The solution uses the `.slnx` format shipped with the .NET 10 SDK.)

## Layout
```
src/BybitDownloader.Core/
  Models/        Candle, Instrument, Ticker, MarketCategory
  Api/           BybitParser (pure JSON->models), BybitApiClient, exceptions
  Resilience/    RetryPolicy (exp. backoff + jitter), AdaptiveRateLimiter (shared AIMD limiter)
  Planning/      PeriodSelector (UTC ranges), DownloadPlanner (month chunks, <=1000-candle windows, launch/now
                 clamping, PlanUniverse for many symbols at once)
  Validation/    CandleValidator (sort, dedupe, sanity, gap detection; never fabricates data)
  Screening/     ScreeningSettings + presets, TickerMetricsCalculator, LiquidityFilterService, ActivityAnalyzer,
                 UniverseScreener (live snapshot -> ticker filter -> optional candle-activity filter)
src/BybitDownloader.Storage/
  CandleStore    ParquetCandleStore (one file per symbol-month, atomic tmp+rename)
  ChunkManifest  SqliteChunkManifest (per-chunk Pending/Done/Failed for resumability)
  FileValidation CandleFileValidator + FileValidationAnalyzer (offline integrity/completeness scan)
src/BybitDownloader.Download/
  DownloadOrchestrator  plan -> N parallel workers -> validate -> store -> manifest (resumable, progress-reporting)
  DownloadJobRunner      job (screen/resolve) -> plan -> download; ForPlanning() previews without touching disk
src/BybitDownloader.Cli/
  bybit-dl               console front end: argument parsing + live console progress + `validate` command
src/BybitDownloader.Gui/
  App / Program          Avalonia 12 desktop host (Fluent theme, Inter font)
  MainWindow (.axaml)    compiled-binding form + progress list
  Services/              DownloadService (IDownloadService): owns HttpClient + limiter, opens store/manifest per run
  ViewModels/            MainWindowViewModel (job building, progress marshalling, commands), ObservableObject, commands
tests/BybitDownloader.Core.Tests/     xUnit, mock HTTP, virtual clock (no real sleeping, no network)
tests/BybitDownloader.Storage.Tests/  Parquet round-trip, atomic write, manifest transitions, file validation
tests/BybitDownloader.Download.Tests/ orchestrator: merge/dedupe, resume, failures, progress, real-storage integration
tests/BybitDownloader.Cli.Tests/      argument/period parsing
tests/BybitDownloader.Gui.Tests/      view-model job building/progress (fake service) + headless Avalonia window smoke test
tools/LiveSmoke/                      throwaway console app that hits the live API (not in the solution)
```

## Design notes
* All dates are UTC, ranges half-open `[start, end)`. Offsets passed to `PeriodSelector.Custom` are converted, not reinterpreted.
* Chunks are month-aligned (one chunk = one symbol-month slice, <= ~44,640 candles) so they map 1:1 to Parquet partitions.
  Requests inside a chunk are windows of <= 1000 candles with **inclusive** start/end.
* Bybit returns klines newest-first; `ParseKlines` preserves response order and `CandleValidator` sorts.
* Forming candle excluded by default (`includeFormingCandle=false`).
* One `AdaptiveRateLimiter` + one `RetryPolicy` must be shared by all workers.

## Storage (Stage 2)
* **Parquet layout:** `{root}/{category}/{symbol}/{yyyy-MM}.parquet` (Parquet.Net 6.1.0, Snappy). Schema:
  `timestamp_ms int64` + `open/high/low/close/volume/turnover decimal(38,10)`. One row group per file.
* **Atomic writes:** data is written to a unique `.tmp-*` file in the same directory, then `File.Move(overwrite)`
  onto the target. A crash never leaves a half-written file that looks complete, and a failed write never
  clobbers the previous good file.
* **Manifest:** SQLite (`Microsoft.Data.Sqlite`, WAL). One row per chunk keyed by the deterministic
  `DownloadChunk.Id` — which is **month-stable** (`{category}/{symbol}/{yyyyMM}`), so re-running a still-forming
  month *updates* its row instead of appending a new one (legacy timestamped keys are collapsed to this form on
  open). The row stores status, row count, expected candles, coverage, file path and human-readable issues.
  `Pending -> Done/Failed`; a re-run **skips a chunk only when it is `Done`, its Parquet file exists, and the
  recorded window still covers the newly planned one** — so a month that has grown since the last run is
  re-fetched and extended, while closed months are skipped.
* **Orchestrator:** `DownloadOrchestrator.RunAsync(plan, IProgress<DownloadProgress>, ct)`. Chunks are independent,
  so they run across `MaxParallelWorkers` on the shared client (one limiter/retry); each chunk's request windows run
  sequentially and all rows are merged, validated, then persisted. Options: `SkipCompleted` (default true) and
  `FailFast` (default false — one bad chunk is recorded as `Failed` and the rest continue). Progress reports
  `(completed, total, candles, outcome)` after every chunk — this is the shape the future GUI will bind to.
* A plan that maps two chunks to the same Parquet file is rejected up front (merge overlapping ranges first).

## Screening pipeline (Stage 3)
* `UniverseScreener.FetchAsync(categories, ct)` pulls `instruments-info` + `tickers` per category into an immutable
  `UniverseSnapshot` (tickers keyed by symbol).
* **Stage 1 (ticker):** `ScreenAsync` runs `LiquidityFilterService` over the snapshot — perpetual + Trading + min
  24h turnover / OI value / 24h range / max spread. Every rejection carries a human-readable reason; a missing metric
  never silently passes an enabled filter.
* **Stage 2 (candle activity, optional):** when `Settings.Activity` is set and an explicit `ActivitySample` window is
  given, each stage-1 survivor's recent klines are fetched (windowed to <=1000 candles), validated, and scored by
  `ActivityAnalyzer` (non-zero range/close fraction, median range %, ATR %, coverage). Symbols that fail are dropped
  with reasons; per-symbol `ApiException`s are collected in `UniverseScreenResult.Errors` and excluded, never thrown.
* **Output -> planning:** `UniverseScreenResult.Selected` is a list of `SelectedSymbol(category, symbol, launchTimeMs,
  metrics)`; feed it straight into `DownloadPlanner.PlanUniverse(...)` (which dedupes `(category, symbol)` and applies
  each symbol's launch clamp). Live check (`tools/LiveSmoke`): 893 linear contracts evaluated, 225 passed `Balanced`.

## CLI (Stage 4)
`bybit-dl` (`src/BybitDownloader.Cli`, assembly name `bybit-dl`) is the end-to-end host. It parses a period +
symbol selection into a `DownloadJob`; `DownloadJobRunner` then resolves symbols, plans, and downloads.
```
dotnet run --project src/BybitDownloader.Cli -- \
  --symbol BTCUSDT --from 2024-01-01 --to 2024-01-31 --out D:/hist      # explicit symbols
dotnet run --project src/BybitDownloader.Cli -- \
  --screen --preset balanced --year 2024 --out D:/hist                  # live screening
dotnet run --project src/BybitDownloader.Cli -- \
  --screen --preset strict --year 2024 --activity-minutes 10080 --plan-only
dotnet run --project src/BybitDownloader.Cli -- \
  validate --out D:/hist                                              # offline integrity/completeness scan
```
* Periods: `--year` / `--month` (repeatable), `--years`, `--from`+`--to` (inclusive dates), `--all`; overlaps merge.
* `--screen` (with `--preset broad|balanced|strict`) or `--symbol` (repeatable) selects symbols.
* `--activity-minutes N` adds the candle-activity stage (needs a preset with activity thresholds, i.e. `strict`).
* `--out` (default `data`), `--db` (default `<out>/manifest.db`), `--workers` (default 4), `--include-forming`.
* `--plan-only` prints the workload (symbols/chunks/candles/requests) and downloads nothing, creating no files.
* `validate` runs the offline validator (see below): `--out`, `--db`, `--symbol`, `--category`.
* Re-running the same command resumes: chunks already `Done` with their Parquet present are skipped.
* Exit codes: `0` success, `1` usage/config error, `2` some chunks failed, `130` cancelled (Ctrl+C).

## GUI (Stage 5)
`src/BybitDownloader.Gui` is an Avalonia 12 desktop app sharing the same `DownloadJob` / `DownloadJobRunner` as the CLI.
```
dotnet run --project src/BybitDownloader.Gui
```
* Form: period mode (year / month / date range / all history), category (linear / inverse / both), symbol mode
  (screen with a preset, or an explicit symbol list), output folder, worker count, include-forming-candle.
* **Preview plan** resolves/screens and shows symbols/chunks/candles/estimated requests without downloading or
  creating files; **Start download** streams `IProgress<DownloadProgress>` into a live per-chunk list; **Cancel**
  stops cleanly (`CancellationToken`); **Validate output** runs the offline validator on the output folder and lists
  per-file `ok`/`warn`/`error` rows.
* The view model is pure BCL and injects a `post` delegate for UI-thread marshalling, so it is unit-tested with a
  fake `IDownloadService`; a headless Avalonia test loads the window to guard the XAML.
* View models use Avalonia **compiled bindings** (`x:DataType`), so binding paths are checked at build time.

## Offline validation
`CandleFileValidator` (`src/BybitDownloader.Storage/FileValidation.cs`) re-reads every `*.parquet` under the output
root (`{category}/{symbol}/{yyyy-MM}.parquet`) and checks **integrity + completeness** with **no network**. It never
modifies data. Run it from the CLI (`bybit-dl validate`) or the GUI's **Validate output** button.
```
dotnet run --project src/BybitDownloader.Cli -- validate --out D:/hist [--symbol BTCUSDT] [--category linear]
```
* Integrity: the file opens and every row decodes; timestamps are 1-minute aligned, strictly ordered, and unique;
  each candle passes the OHLCV sanity checks (`CandleValidator.IsSane`); gaps are counted.
* Completeness: row count vs the expected candle count, coverage ratio, and a cross-check against the manifest
  (chunk status, recorded row counts, and entries marked `Done` whose Parquet file is missing).
* Each file is `ok`, `warn`, or `error`, with the reasons listed inline; a summary counts files, rows, and missing
  minutes. Warnings (gaps, partial coverage, unrecorded files) do not fail the run; structural errors and
  manifest/disk mismatches do. Exit codes: `0` clean, `2` problems found, `1` configuration error.
* Expected-candle windows come from the manifest chunk when present, otherwise from the month length clamped to the
  Bybit epoch and the current month clamped to "now".

## Assumptions verified against the live API (2026-10-09)
Verified by `tools/LiveSmoke` (throwaway console app, not in the solution). Original assumptions:

1. Kline `start`/`end` both inclusive, `limit` max 1000, response newest-first. **CONFIRMED.**
   Caveat: when the range spans more than `limit` candles, Bybit returns the **newest** `limit` candles
   (anchored at `end`), silently dropping the oldest. Windows must be <= 1000 candles — the planner already
   splits on that bound.
2. Rate-limit headers `X-Bapi-Limit`, `X-Bapi-Limit-Status`, `X-Bapi-Limit-Reset-Timestamp`. **WRONG for this
   use case.** Public `/v5/market/*` endpoints return NO `X-Bapi-*` headers (the API rate limit is per-UID and
   only surfaced on authenticated endpoints), so `AdaptiveRateLimiter.ReportHeaders` is inert here. Public
   traffic is governed by the **IP limit: 600 requests / 5s** shared across `api.bybit.com`, signalled by
   `403 access too frequent`. The limiter therefore runs at its configured rate and reacts to 429/403/10006.
3. Transient retCodes {10000, 10006, 10016, 10018} (`Api/Exceptions.cs`). **PARTIALLY CONFIRMED.** Docs list
   10000 (server timeout), 10006 (too many visits), 10016 (server error / restarting); 10018 is a legacy
   IP-limit code. Docs also list 10429 "System level frequency protection" as transient.
4. Default limiter rate (5 req/s, max 20) is deliberately conservative — the IP cap is 600/5s. Safe; raise later.
5. Inverse contracts: `volume24h` / `openInterest` (contract counts, 1 contract = 1 USD) used as USD turnover /
   OI (flagged `Approximate`). **CONFIRMED.** For BTCUSD: `openInterest=515,529,960` and
   `volume24h=380,245,434` (~USD), while `openInterestValue=6250.70` and `turnover24h=4646.89` are base coin
   (BTC). The calculator correctly picks the USD-denominated fields for inverse.
   NOTE: inverse **kline** `volume` is quote coin (USD) and `turnover` is base coin (BTC) per the kline docs.
6. HTTP 403 treated as an IP-level limit (10 min block). **CONFIRMED as the signal** ("403, access too
   frequent"), though the fixed 10-minute block length is still a guess.

## Known limitations (so far)
* Current ticker screening reflects TODAY's market and creates survivorship bias if used to define a historical universe.
* A symbol-month has exactly one Parquet file. Downloading the *same* month with two different partial-month ranges
  would overwrite it; the orchestrator rejects duplicate targets within one plan but not across separate runs.
* Parallelism is per-chunk (a month). A single small chunk cannot use more than one worker or one limiter slot.
* 1-minute completeness assumes 24/7 trading. Symbols that mirror equities/indices (e.g. `AAPLUSDT`, `NVDAUSDT`)
  trade only during market hours, so their files legitimately show gaps — the validator reports these as
  **warnings**, not errors (a missing minute can also be a genuine zero-trade minute).
* The current (still-forming) month is only complete up to the moment of the last run; re-running re-fetches and
  extends it (see the manifest note above).

## Roadmap
* **Stage 3 — Screening pipeline (done):** `UniverseScreener` wires `LiquidityFilterService` / `ActivityAnalyzer` to a
  live snapshot; `DownloadPlanner.PlanUniverse` consumes the selected symbols.
* **Stage 4 — CLI host (done):** `bybit-dl` + `DownloadJobRunner` run a `DownloadJob` end to end with Ctrl+C cancel and
  a console progress view.
* **Stage 5 — GUI (Avalonia) (done):** `BybitDownloader.Gui` binds the `IProgress<DownloadProgress>` stream and
  `DownloadJobRunner` (via `IDownloadService`) to a live download view, with preview, start and cancel.
* **Offline validator (done):** `CandleFileValidator` + `bybit-dl validate` + the GUI's **Validate output** button
  re-read the output tree and report integrity + completeness (with manifest cross-check).
