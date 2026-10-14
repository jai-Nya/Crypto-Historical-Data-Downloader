# BybitDownloader — Multi-Exchange Historical Candle Downloader

**Status: Core + storage + orchestrator + screening + CLI + GUI + offline validator + 5-exchange adapters, all tested.**
Everything here has been compiled and tested (`dotnet test` -> 215 passing, no network in tests; live smoke runs
passed for Bybit, Binance spot, OKX, Kraken futures and KuCoin futures/spot on 2026-10-14).

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
  Models/        Candle, Instrument, Ticker, MarketCategory, Exchange (bybit|binance|okx|kraken|kucoin)
  Api/           IExchangeMarketData + one client per exchange (Bybit/Okx one-class, Binance/Kraken/KuCoin
                 two-class curl+parser); ExchangeHttpClient sets BaseAddress; ExchangeClientFactory; ExchangeProfiles
                 (per-exchange/per-category rate limit, retry, max candles per request, OI availability)
  Resilience/    RetryPolicy (exp. backoff + jitter), AdaptiveRateLimiter (shared AIMD limiter)
  Planning/      PeriodSelector (UTC ranges), DownloadPlanner (month chunks, windowed by the exchange's max candles,
                 launch/now clamping, PlanUniverse for many symbols at once)
  Validation/    CandleValidator (sort, dedupe, sanity, gap detection; never fabricates data)
  Screening/     ScreeningSettings + presets, TickerMetricsCalculator, LiquidityFilterService, ActivityAnalyzer,
                 UniverseScreener (live snapshot -> ticker filter -> optional candle-activity filter; TradFi excluded,
                 requires perpetuals; the OI filter is dropped for markets without OI support)
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
  App / Program          Avalonia 12 desktop host (Fluent dark theme + the DownloaderGui design system, Segoe UI Variable)
  MainWindow (.axaml)    compiled-binding wizard: step dots, exchange tiles, live monitor
  Converters/            Equals (radio/selection binding), HexToBrush (palette strings -> brushes),
                         StepDotOpacity (step index -> progress-dot opacity)
  Services/              DownloadService (IDownloadService): owns HttpClient + limiter, opens store/manifest per run
  ViewModels/            MainWindowViewModel (job building, progress marshalling, commands), ObservableObject, commands
tests/BybitDownloader.Core.Tests/     xUnit, mock HTTP, virtual clock (no real sleeping, no network)
tests/BybitDownloader.Storage.Tests/  Parquet round-trip, atomic write, manifest transitions, file validation
tests/BybitDownloader.Download.Tests/ orchestrator: merge/dedupe, resume, failures, progress, real-storage integration
tests/BybitDownloader.Cli.Tests/      argument/period parsing
tests/BybitDownloader.Gui.Tests/      view-model job building/progress (fake service) + headless Avalonia window smoke test
```

## Design notes
* All dates are UTC, ranges half-open `[start, end)`. Offsets passed to `PeriodSelector.Custom` are converted, not reinterpreted.
* Chunks are month-aligned (one chunk = one symbol-month slice, <= ~44,640 candles) so they map 1:1 to Parquet partitions.
  Requests inside a chunk are windows of <= the exchange's max candles per request (**inclusive** start/end), sized by
  `ExchangeProfiles` (Bybit 1000, Binance 1000, OKX 300, Kraken 720, KuCoin 500 futures / 1500 spot).
* **One exchange per run** — every market call goes through the `ExchangeClientFactory`-created client and the run is
  root partitioned by `{exchange}/`, so separate runs for different exchanges never collide.
* Bybit returns klines newest-first; `ParseKlines` preserves response order and `CandleValidator` sorts. Kraken
  futures charts and KuCoin futures klines are oldest-first with per-page caps (2000 / 200 rows); both clients
  forward-page until the window end is covered and re-fetch pages that stall under throttling.
* Forming candle excluded by default (`includeFormingCandle=false`).
* One `AdaptiveRateLimiter` + one `RetryPolicy` must be shared by all workers.

## Storage (Stage 2)
* **Parquet layout:** `{root}/{exchange}/{category}/{symbol}/{yyyy-MM}.parquet` (Parquet.Net 6.1.0, Snappy); exchange is
  the exchange slug (`bybit|binance|okx|kraken|kucoin`). Schema:
  `timestamp_ms int64` + `open/high/low/close/volume/turnover decimal(38,10)`. One row group per file.
* **Atomic writes:** data is written to a unique `.tmp-*` file in the same directory, then `File.Move(overwrite)`
  onto the target. A crash never leaves a half-written file that looks complete, and a failed write never
  clobbers the previous good file.
* **Manifest:** SQLite (`Microsoft.Data.Sqlite`, WAL). One row per chunk keyed by the deterministic
  `DownloadChunk.Id` — which is **month-stable** (`{exchange}/{category}/{symbol}/{yyyyMM}`), so re-running a
  still-forming month *updates* its row instead of appending a new one (legacy keys from the pre-exchange
  `{category}/{symbol}/{yyyyMM}` form and the even older timestamped form are collapsed to this on open, keeping the
  widest `Done` row per symbol-month). The row stores status, row count, expected candles, coverage, file path and
  human-readable issues.
  `Pending -> Done/Failed`; a re-run **skips a chunk only when it is `Done`, its Parquet file exists, and the
  recorded window still covers the newly planned one** — so a month that has grown since the last run is
  re-fetched and extended, while closed months are skipped.
* **Orchestrator:** `DownloadOrchestrator.RunAsync(plan, IProgress<DownloadProgress>, ct)`. Chunks are independent,
  so they run across `MaxParallelWorkers` (default 8) on the shared client (one limiter/retry); each chunk's request
  windows are fetched concurrently (`MaxWindowConcurrency`, default 4) and all rows are merged, validated, then
  persisted. Options: `SkipCompleted` (default true) and `FailFast` (default false — one bad chunk is recorded as
  `Failed` and the rest continue). Progress reports `(completed, total, candles, outcome)` after every chunk — this
  is the shape the future GUI will bind to.
* A plan that maps two chunks to the same Parquet file is rejected up front (merge overlapping ranges first).

## Screening pipeline (Stage 3)
* `UniverseScreener.FetchAsync(categories, ct)` pulls `instruments-info` + `tickers` per category into an immutable
  `UniverseSnapshot` (tickers keyed by symbol).
* **Stage 1 (ticker):** `ScreenAsync` runs `LiquidityFilterService` over the snapshot — perpetual + Trading + min
  24h turnover / OI value / 24h range / max spread. Every rejection carries a human-readable reason; a missing metric
  never silently passes an enabled filter. TradFi contracts (`symbolType` = stock/commodity/forex/ETF, or a non-empty
  `marketRegion`) are excluded unless `Settings.IncludeTradFi` is set.
* **Stage 2 (candle activity, optional):** when `Settings.Activity` is set and an explicit `ActivitySample` window is
  given, each stage-1 survivor's recent klines are fetched concurrently (`ActivityMaxConcurrency`, default 8;
  each symbol's own windowing stays sequential), validated, and scored by
  `ActivityAnalyzer` (non-zero range/close fraction, median range %, ATR %, coverage). Symbols that fail are dropped
  with reasons; per-symbol `ApiException`s are collected in `UniverseScreenResult.Errors` and excluded, never thrown.
* **Output -> planning:** `UniverseScreenResult.Selected` is a list of `SelectedSymbol(category, symbol, launchTimeMs,
  metrics)`; feed it straight into `DownloadPlanner.PlanUniverse(...)` (which dedupes `(category, symbol)` and applies
  each symbol's launch clamp). A live check evaluated 893 linear contracts, 225 of which passed `Balanced`.

## CLI (Stage 4)
`bybit-dl` (`src/BybitDownloader.Cli`, assembly name `bybit-dl`) is the end-to-end host. It parses an exchange, a
period + symbol selection into a `DownloadJob`; `DownloadJobRunner` then resolves symbols, plans, and downloads.
```
dotnet run --project src/BybitDownloader.Cli -- \
  --exchange bybit --symbol BTCUSDT --from 2024-01-01 --to 2024-01-31 --out D:/hist   # explicit symbols
dotnet run --project src/BybitDownloader.Cli -- \
  --exchange kraken --category linear --symbol PF_XBTUSD --from 2026-10-10 --to 2026-10-11 --out D:/hist
dotnet run --project src/BybitDownloader.Cli -- \
  --screen --preset balanced --year 2024 --out D:/hist                  # live screening
dotnet run --project src/BybitDownloader.Cli -- \
  --screen --preset strict --year 2024 --activity-minutes 10080 --plan-only
dotnet run --project src/BybitDownloader.Cli -- \
  validate --out D:/hist                                              # offline integrity/completeness scan
```
* `--exchange NAME` selects the market (default `bybit`; one of `bybit|binance|okx|kraken|kucoin`). All symbols,
  screening callbacks and storage prefix under the chosen exchange.
* Periods: `--year` / `--month` (repeatable), `--years`, `--from`+`--to` (inclusive dates), `--all`; overlaps merge.
* `--category` accepts `linear|inverse` (derivatives) and also `spot`, `both` (linear+inverse), `all`
  (spot+linear+inverse) where the exchange supports them. Screen presets default to derivatives.
* `--screen` (with `--preset broad|balanced|strict`) or `--symbol` (repeatable) selects symbols.
* `--include-tradfi` includes TradFi contracts (stocks, commodities, forex, ETFs); excluded by default.
* `--activity-minutes N` adds the candle-activity stage (needs a preset with activity thresholds, i.e. `strict`).
* `--out` (default `data`), `--db` (default `<out>/manifest.db`), `--workers` (default 8), `--include-forming`.
* `--plan-only` prints the workload (symbols/chunks/candles/requests) and downloads nothing, creating no files.
* `validate` runs the offline validator (see below): `--out`, `--db`, `--symbol`, `--category`.
* Re-running the same command resumes: chunks already `Done` with their Parquet present are skipped.
* Exit codes: `0` success, `1` usage/config error, `2` some chunks failed, `130` cancelled (Ctrl+C).

## GUI (Stage 5)
`src/BybitDownloader.Gui` is an Avalonia 12 desktop app sharing the same `DownloadJob` / `DownloadJobRunner` as the CLI.
```
dotnet run --project src/BybitDownloader.Gui
```
* **Step wizard** (Next / Back), then a live monitor. The window starts on a plain black canvas; picking an exchange
  darkens it to a 20% tint of that exchange's brand accent:
  1. **Exchange** - five large tiles (Bybit / Binance / OKX on top, Kraken / KuCoin below), one exchange per run.
  2. **Markets & symbols** - three market-type checkboxes (**Spot** / **Linear** / **Inverse**, default Linear+Inverse);
     symbol mode is *screen the universe* (with a **broad/balanced/strict** preset and a **TradFi** checkbox) or
     *specific symbols* (a comma/space/newline-separated list).
  3. **Date range** - Year / Month / Range / All buttons show only the inputs they need (year dropdown; year+month;
     from/to `yyyy-MM-dd`; or nothing for all history). Dates are always UTC.
  4. **Output & options** - output folder (text box + **Browse** via the OS folder picker), the derived manifest path,
     parallel workers, and include-forming-candle.
  5. **Review & start** - a plain-text summary of the resolved selections, plus **Preview plan** (screens/resolves and
     shows symbols/chunks/candles/estimated requests without downloading or touching disk) and **Validate existing
     output**.
  6. **Monitor** - progress bar, status line (current symbol), a per-chunk list (category / symbol / period / status /
     rows), and a scrolling **log**. **Cancel** stops cleanly; **New run** returns to step 1. The header shows the six
     step dots plus the current step's title and one-line hint, and validation errors appear in red in the footer.
* **Live telemetry:** the core libraries emit `TelemetryEvent`s (`ChunkStarted`, `ChunkCompleted`, `Retry`, `Log`)
  through the `ITelemetry` abstraction (`src/BybitDownloader.Core/Telemetry.cs`). The orchestrator wraps each chunk in
  an ambient `DownloadScope`, so the HTTP layer attributes a `RetryPolicy` retry to the chunk being fetched. The GUI
  marshals both `IProgress<DownloadProgress>` and `ITelemetry` onto the UI thread (`post` delegate); the CLI ignores
  telemetry. `ITelemetry`/`DownloadScope` are optional, so the core stays usable without a UI.
* The view model is pure BCL and injects a `post` delegate for UI-thread marshalling, so it is unit-tested with a
  fake `IDownloadService`; a headless Avalonia test loads the window to guard the XAML.
* View models use Avalonia **compiled bindings** (`x:DataType`), so binding paths are checked at build time.

## Offline validation
`CandleFileValidator` (`src/BybitDownloader.Storage/FileValidation.cs`) re-reads every `*.parquet` under the output
root (`{category}/{symbol}/{yyyy-MM}.parquet`) and checks **integrity + completeness** with **no network**. It never
modifies data. Run it from the CLI (`bybit-dl validate`) or the GUI's **Validate output** button.
```
dotnet run --project src/BybitDownloader.Cli -- validate --out D:/hist [--exchange bybit] [--symbol BTCUSDT] [--category linear]
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

## Exchange support (2026-10-14)
Every adapter is behind `IExchangeMarketData`/`ExchangeClientFactory` and was smoke-tested live for a 2-day 1m window
(2,880 candles) unless noted. Per-exchange tuning lives in `ExchangeProfiles` and combines with the shared
`AdaptiveRateLimiter` + `RetryPolicy`.

| Exchange | Base(s) | Categories (live-verified) | Rate limiter initial/max (req/s) | Max candles/request | OI data in screening |
|---|---|---|---|---|---|
| Bybit | API v5 | linear, inverse, spot | 20 / 60 | 1,000 | yes (all categories) |
| Binance | binance.com spot + fapi futures | spot (verified), linear/inverse (adapter built, **not verifiable from this host** — `fapi.binance.com` is geo-blocked, SSL-handshake fails) | 10/30 spot, 2/6 futures | 1,000 | no |
| OKX | okx.com | linear (verified); spot/inverse have endpoints but were not smoke-run | 4 / 8 | 300 | no |
| Kraken | Kraken spot + futures charts | linear futures (verified ×3, full 2,880); spot OHLC (built, ≤720 candles and no backward paging — only recent+mid history retrievable) | 1/2 spot, 2/5 futures | 720 | no |
| KuCoin | KuCoin v2 | linear futures (verified; futures 1m history only exists from 2025-01-01 and the kline endpoint caps each page at 200 candles — the client pages forward internally; data has scattered missing minutes), spot (verified) | 5/15 spot, 3/8 futures | 500 futures, 1,500 spot | no |

Notes:
* Futures-history availability varies: Kraken futures charts cover a rolling recent window (deep history is not
  retrievable), KuCoin futures 1m klines start at 2025-01-01, and Binance futures are unreachable from this host.
  Bybit/OKX full history is available from each exchange's epoch.
* When only spot is selected, the perpetual-only screening filters are skipped; the OI value filter is dropped for any
  category the exchange does not publish OI for (`AdjustForExchange`).
* Rate limits are conservative defaults with headroom for the blind AIMD probe; raise them via `ExchangeProfiles` if
  you are far from the exchange's IP ceiling.

## Assumptions verified against the live API (2026-10-09)
Verified live specifically against **Bybit** on 2026-10-09 (the numbers below are Bybit-specific; per-exchange
behavior for the other four is described above). Original assumptions:

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
4. Default limiter rate ramps from 20 req/s and caps at 60 req/s (`MaxRatePerSecond`) — roughly half the
   600/5s IP ceiling, leaving headroom so the AIMD probe (which runs blind, since public endpoints expose no
   `X-Bapi-*` headers) never sails into a 403 ban. Raising it further trades margin for speed.
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
* Parallelism is two-level: chunks across `MaxParallelWorkers` (default 8) and request windows within a chunk across
  `MaxWindowConcurrency` (default 4), all sharing one limiter slot scheduler.
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
