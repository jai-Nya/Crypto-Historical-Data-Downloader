using System.Globalization;
using System.Text.Json;
using BybitDownloader.Core.Api;
using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using BybitDownloader.Core.Screening;
using BybitDownloader.Download;
using BybitDownloader.Storage;

var http = new HttpClient { BaseAddress = new Uri("https://api.bybit.com") };
http.DefaultRequestHeaders.Add("User-Agent", "BybitDownloader-LiveSmoke/1.0");

static void H(string title)
{
    Console.WriteLine();
    Console.WriteLine("========================================================");
    Console.WriteLine(title);
    Console.WriteLine("========================================================");
}

static void PrintBapiHeaders(HttpResponseMessage r)
{
    var any = false;
    foreach (var h in r.Headers.Where(h => h.Key.StartsWith("X-Bapi", StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine($"  {h.Key}: {string.Join(",", h.Value)}");
        any = true;
    }
    if (!any) Console.WriteLine("  (no X-Bapi-* headers present)");
}

// ---------------------------------------------------------------
// 1. Kline ordering and bounds
// ---------------------------------------------------------------
H("1. KLINE ordering + bounds (linear BTCUSDT, interval=1, 60 rows)");
var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
var end = start + 59 * 60_000;
var klineUrl = $"/v5/market/kline?category=linear&symbol=BTCUSDT&interval=1&start={start}&end={end}&limit=1000";
var kresp = await http.GetAsync(klineUrl);
Console.WriteLine($"  GET {klineUrl}");
Console.WriteLine($"  HTTP {(int)kresp.StatusCode}");
PrintBapiHeaders(kresp);
var kbody = await kresp.Content.ReadAsStringAsync();
var candles = BybitParser.ParseKlines(kbody);
Console.WriteLine($"  parsed rows      : {candles.Count}  (expected 60)");
if (candles.Count > 0)
{
    Console.WriteLine($"  response order   : first(in-list)={candles[0].TimestampMs}  last(in-list)={candles[^1].TimestampMs}");
    Console.WriteLine($"  newest-first?    : {(candles[0].TimestampMs > candles[^1].TimestampMs ? "YES" : "NO")}");
    Console.WriteLine($"  requested start  : {start}  endInclusive={end}");
    var asc = candles.OrderBy(c => c.TimestampMs).ToList();
    Console.WriteLine($"  min ts == start? : {(asc[0].TimestampMs == start ? "YES" : "NO")}  (min={asc[0].TimestampMs})");
    Console.WriteLine($"  max ts == end?   : {(asc[^1].TimestampMs == end ? "YES" : "NO")}  (max={asc[^1].TimestampMs})");
    Console.WriteLine($"  span (min->max)  : {asc[^1].TimestampMs - asc[0].TimestampMs} ms  (~{((asc[^1].TimestampMs - asc[0].TimestampMs) / 60000.0):0.#} min)");
    Console.WriteLine($"  first candle     : ts={asc[0].TimestampMs} o={asc[0].Open} h={asc[0].High} l={asc[0].Low} c={asc[0].Close} v={asc[0].Volume} t={asc[0].Turnover}");

    using var doc = JsonDocument.Parse(kbody);
    var rawList = doc.RootElement.GetProperty("result").GetProperty("list");
    Console.WriteLine($"  RAW first row    : {rawList[0]}");
    Console.WriteLine("  RAW first-row element types:");
    foreach (var el in rawList[0].EnumerateArray())
        Console.WriteLine($"      {el.ValueKind,-8} {(el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString())}");
    Console.WriteLine($"  RAW row width    : {rawList[0].GetArrayLength()} fields");
}

// ---------------------------------------------------------------
// 2. Row shape (explicit 7-field check)
// ---------------------------------------------------------------
H("2. ROW SHAPE");
using (var doc2 = JsonDocument.Parse(kbody))
{
    var list = doc2.RootElement.GetProperty("result").GetProperty("list");
    var widths = new SortedSet<int>();
    var allStrings = true;
    foreach (var row in list.EnumerateArray())
    {
        widths.Add(row.GetArrayLength());
        foreach (var el in row.EnumerateArray())
            if (el.ValueKind != JsonValueKind.String) allStrings = false;
    }
    Console.WriteLine($"  distinct row widths : [{string.Join(",", widths)}]  (expected [7])");
    Console.WriteLine($"  all fields strings? : {(allStrings ? "YES" : "NO")}  (expected YES)");
}

// ---------------------------------------------------------------
// 3. Instruments (linear + inverse)
// ---------------------------------------------------------------
H("3. INSTRUMENTS (linear + inverse)");
foreach (var cat in new[] { MarketCategory.Linear, MarketCategory.Inverse })
{
    var url = $"/v5/market/instruments-info?category={cat.ToApiString()}&limit=1000";
    var r = await http.GetAsync(url);
    var body = await r.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(body);
    var result = doc.RootElement.GetProperty("result");
    var list = result.GetProperty("list");
    var count = list.GetArrayLength();
    var contractTypes = new SortedSet<string>();
    var statuses = new SortedSet<string>();
    foreach (var e in list.EnumerateArray())
    {
        contractTypes.Add(e.TryGetProperty("contractType", out var ct) ? ct.GetString() ?? "" : "");
        statuses.Add(e.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "");
    }
    var cursor = result.TryGetProperty("nextPageCursor", out var c) ? c.GetString() : null;
    Console.WriteLine($"  [{cat.ToApiString()}] HTTP {(int)r.StatusCode}, items on page={count}");
    Console.WriteLine($"    contractType values : {string.Join(", ", contractTypes)}");
    Console.WriteLine($"    status values       : {string.Join(", ", statuses)}");

    var sample = list[0];
    var launch = sample.TryGetProperty("launchTime", out var lt) ? lt.GetString() : "(missing)";
    var launchParsed = BybitParser.ParseInstruments(body, cat).Items[0].LaunchTimeMs;
    Console.WriteLine($"    sample symbol       : {sample.GetProperty("symbol").GetString()}");
    Console.WriteLine($"    sample launchTime   : raw='{launch}' -> parsedMs={launchParsed}");
    Console.WriteLine($"    nextPageCursor      : {(string.IsNullOrEmpty(cursor) ? "(empty string / absent)" : cursor)}");
}

// ---------------------------------------------------------------
// 4. Rate-limit headers (already shown above; do one more clean call)
// ---------------------------------------------------------------
H("4. RATE-LIMIT HEADERS");
var r4 = await http.GetAsync("/v5/market/tickers?category=linear&symbol=BTCUSDT");
Console.WriteLine($"  HTTP {(int)r4.StatusCode}");
PrintBapiHeaders(r4);

// ---------------------------------------------------------------
// 5. Inverse ticker fields (BTCUSD)
// ---------------------------------------------------------------
H("5. INVERSE TICKER (BTCUSD)");
var r5 = await http.GetAsync("/v5/market/tickers?category=inverse");
var b5 = await r5.Content.ReadAsStringAsync();
using (var doc5 = JsonDocument.Parse(b5))
{
    var list = doc5.RootElement.GetProperty("result").GetProperty("list");
    JsonElement? btc = null;
    foreach (var e in list.EnumerateArray())
        if (e.GetProperty("symbol").GetString() == "BTCUSD") { btc = e; break; }
    if (btc is { } t)
    {
        Console.WriteLine("  RAW BTCUSD fields:");
        foreach (var p in t.EnumerateObject())
            Console.WriteLine($"      {p.Name,-20} = {p.Value}");
    }
    else Console.WriteLine("  BTCUSD not found in inverse tickers");
}

var r5b = await http.GetAsync("/v5/market/tickers?category=inverse&symbol=BTCUSD");
var b5b = await r5b.Content.ReadAsStringAsync();
var tick = BybitParser.ParseTickers(b5b).First();
var instInverse = new Instrument("BTCUSD", MarketCategory.Inverse, "InversePerpetual", "Trading", "BTC", "USD", "BTC", null, 0.1m);
var metrics = TickerMetricsCalculator.Compute(instInverse, tick);
Console.WriteLine();
Console.WriteLine("  Parsed BTCUSD ticker:");
Console.WriteLine($"    lastPrice={tick.LastPrice} high24h={tick.HighPrice24h} low24h={tick.LowPrice24h}");
Console.WriteLine($"    turnover24h={tick.Turnover24h} volume24h={tick.Volume24h}");
Console.WriteLine($"    openInterest={tick.OpenInterest} openInterestValue={tick.OpenInterestValue}");
Console.WriteLine($"    bid1={tick.Bid1Price} ask1={tick.Ask1Price}");
Console.WriteLine("  TickerMetricsCalculator.Compute (inverse assumption in action):");
Console.WriteLine($"    TurnoverQuote      = {metrics.TurnoverQuote} {metrics.Currency}   (from volume24h)");
Console.WriteLine($"    OpenInterestQuote  = {metrics.OpenInterestValueQuote} {metrics.Currency}   (from openInterest)");
Console.WriteLine($"    Approximate        = {metrics.Approximate}");

// ---------------------------------------------------------------
// 6. Limit cap: ask for 5000 candles, expect exactly 1000 (assumption 1)
// ---------------------------------------------------------------
H("6. KLINE limit cap (request 5000 min range, limit=1000)");
var bigStart = start;
var bigEnd = start + 4999 * 60_000;
var bigUrl = $"/v5/market/kline?category=linear&symbol=BTCUSDT&interval=1&start={bigStart}&end={bigEnd}&limit=1000";
var bigBody = await (await http.GetAsync(bigUrl)).Content.ReadAsStringAsync();
var big = BybitParser.ParseKlines(bigBody);
Console.WriteLine($"  parsed rows : {big.Count}  (expect 1000)");
if (big.Count > 0)
{
    var a = big.OrderBy(c => c.TimestampMs).ToList();
    Console.WriteLine($"  list order  : newest-first? {(big[0].TimestampMs > big[^1].TimestampMs ? "YES" : "NO")}");
    Console.WriteLine($"  min ts == start? {(a[0].TimestampMs == bigStart ? "YES" : "NO")}  min={a[0].TimestampMs}");
    Console.WriteLine($"  max ts == end?   {(a[^1].TimestampMs == bigEnd ? "YES" : "NO")}  max={a[^1].TimestampMs}");
    Console.WriteLine($"  -> window covers [{a[0].TimestampMs}..{a[^1].TimestampMs}] = {big.Count} candles");
}

// ---------------------------------------------------------------
// 7. Instruments pagination (force tiny page size to exercise cursor)
// ---------------------------------------------------------------
H("7. INSTRUMENTS pagination (linear, limit=50)");
var pages = 0;
string? cur = null;
var seen = new HashSet<string>();
var total = 0;
do
{
    var u = $"/v5/market/instruments-info?category=linear&limit=50" + (cur is null ? "" : $"&cursor={Uri.EscapeDataString(cur)}");
    var b = await (await http.GetAsync(u)).Content.ReadAsStringAsync();
    using var d = JsonDocument.Parse(b);
    total += d.RootElement.GetProperty("result").GetProperty("list").GetArrayLength();
    cur = d.RootElement.GetProperty("result").TryGetProperty("nextPageCursor", out var cc) ? cc.GetString() : null;
    if (string.IsNullOrEmpty(cur)) cur = null;
    pages++;
    if (cur is not null && !seen.Add(cur)) { Console.WriteLine("  CURSOR REPEATED -- abort"); break; }
    Console.Write($"  page {pages}: cumulative={total}, cursor={(cur is null ? "<none>" : cur)}\n");
} while (cur is not null && pages < 40);
Console.WriteLine($"  total pages={pages}, total items={total}");

// ---------------------------------------------------------------
// 8. Real BybitApiClient end-to-end (limiter + retry)
// ---------------------------------------------------------------
H("8. BybitApiClient end-to-end (shared limiter + retry)");
var limiter = new BybitDownloader.Core.Resilience.AdaptiveRateLimiter(
    new BybitDownloader.Core.Resilience.RateLimiterOptions { InitialRatePerSecond = 5, MaxRatePerSecond = 20 });
var retry = new BybitDownloader.Core.Resilience.RetryPolicy(
    new BybitDownloader.Core.Resilience.RetryOptions { MaxAttempts = 3 });
using (var http2 = new HttpClient { BaseAddress = new Uri("https://api.bybit.com") })
{
    var client = new BybitApiClient(http2, limiter, retry);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var insts = await client.GetInstrumentsAsync(MarketCategory.Linear, default);
    var tks = await client.GetTickersAsync(MarketCategory.Linear, default);
    var kl = await client.GetKlinesAsync(MarketCategory.Linear, "BTCUSDT", start, start + 9 * 60_000, default);
    sw.Stop();
    Console.WriteLine($"  3 calls OK in {sw.ElapsedMilliseconds} ms");
    Console.WriteLine($"  instruments={insts.Count}, tickers={tks.Count}, klines={kl.Count} (requested 10)");
    var s = limiter.Stats;
    Console.WriteLine($"  limiter: rate={s.CurrentRatePerSecond}/s requests={s.TotalRequests} wait={s.TotalWait} rateLimitEvents={s.RateLimitEvents}");
}

// ---------------------------------------------------------------
// 9. Orchestrator end-to-end (live, tiny range) + resume
// ---------------------------------------------------------------
H("9. ORCHESTRATOR end-to-end (live, 5-minute range)");
var dataRoot = Path.Combine(Path.GetTempPath(), "bybit-livesmoke", Guid.NewGuid().ToString("N"));
var store9 = new ParquetCandleStore(Path.Combine(dataRoot, "data"));
using (var manifest9 = new SqliteChunkManifest(Path.Combine(dataRoot, "manifest.db")))
{
    using var http9 = new HttpClient { BaseAddress = new Uri("https://api.bybit.com") };
    var limiter9 = new BybitDownloader.Core.Resilience.AdaptiveRateLimiter(
        new BybitDownloader.Core.Resilience.RateLimiterOptions { InitialRatePerSecond = 5, MaxRatePerSecond = 20 });
    var retry9 = new BybitDownloader.Core.Resilience.RetryPolicy(
        new BybitDownloader.Core.Resilience.RetryOptions { MaxAttempts = 3 });
    var client9 = new BybitApiClient(http9, limiter9, retry9);
    var orch = new DownloadOrchestrator(client9, store9, manifest9, new OrchestratorOptions { MaxParallelWorkers = 2 });

    var chunk = new DownloadChunk(MarketCategory.Linear, "BTCUSDT", 2024, 1, start, start + 5 * 60_000L);
    var plan = new PlanResult(new[] { chunk }, Array.Empty<UtcRange>());
    Console.WriteLine($"  chunk: {chunk.Id}  candles={chunk.CandleCount}");

    var sum = await orch.RunAsync(plan,
        new Progress<DownloadProgress>(p => Console.WriteLine(
            $"   progress {p.Completed}/{p.Total} {p.Outcome.ChunkId} -> {p.Outcome.Result} rows={p.Outcome.Rows}")));
    Console.WriteLine($"  run1: ok={sum.Succeeded} skip={sum.Skipped} fail={sum.Failed} candles={sum.TotalCandles} elapsed={sum.Elapsed.TotalMilliseconds:0}ms");
    var file = store9.GetPath(chunk);
    Console.WriteLine($"  file exists: {File.Exists(file)}  ({file})");
    var back = await store9.ReadAsync(file);
    Console.WriteLine($"  read back: {back.Count} rows, first={back[0].TimestampMs} last={back[^1].TimestampMs}, close[0]={back[0].Close}");

    var sum2 = await orch.RunAsync(plan);
    Console.WriteLine($"  run2 (resume): ok={sum2.Succeeded} skip={sum2.Skipped} fail={sum2.Failed} (expect skip=1)");
}
try { Directory.Delete(dataRoot, true); } catch { }

// ---------------------------------------------------------------
// 10. Universe screener (live)
// ---------------------------------------------------------------
H("10. UNIVERSE SCREENER (live, linear)");
using (var http10 = new HttpClient { BaseAddress = new Uri("https://api.bybit.com") })
{
    var limiter10 = new BybitDownloader.Core.Resilience.AdaptiveRateLimiter(
        new BybitDownloader.Core.Resilience.RateLimiterOptions { InitialRatePerSecond = 5, MaxRatePerSecond = 20 });
    var retry10 = new BybitDownloader.Core.Resilience.RetryPolicy(
        new BybitDownloader.Core.Resilience.RetryOptions { MaxAttempts = 3 });
    var client10 = new BybitApiClient(http10, limiter10, retry10);
    var screener = new UniverseScreener(client10);

    var screen = await screener.ScreenAsync(
        new UniverseScreenOptions { Categories = [MarketCategory.Linear] }, CancellationToken.None);

    Console.WriteLine($"  evaluated: {screen.InstrumentResults.Count}  eligible(Balanced): {screen.Selected.Count}");
    Console.WriteLine("  top by 24h turnover:");
    foreach (var s in screen.Selected.OrderByDescending(x => x.Metrics.TurnoverQuote).Take(8))
        Console.WriteLine($"    {s.Symbol,-16} turnover={s.Metrics.TurnoverQuote,18:N0} {s.Metrics.Currency}  OI={s.Metrics.OpenInterestValueQuote,16:N0}  range={s.Metrics.PriceRangePct:0.00}%");

    var topReason = screen.InstrumentResults
        .Where(r => !r.IsEligible)
        .SelectMany(r => r.Reasons)
        .GroupBy(x => x.Split(' ')[0] + " " + (x.Split(' ').Length > 1 ? x.Split(' ')[1] : ""))
        .OrderByDescending(g => g.Count())
        .Take(4);
    Console.WriteLine("  most common exclusion reasons:");
    foreach (var g in topReason) Console.WriteLine($"    {g.Count(),4}x  {g.Key}");

    if (screen.Selected.Count >= 3)
    {
        var now10 = DateTimeOffset.UtcNow;
        var sample = UtcRange.Create(now10.AddMinutes(-30), now10);
        var thresholds = new ActivityThresholds(0.5m, null, 0.0001m, null, null);
        var filter10 = new LiquidityFilterService();
        var top3 = screen.Selected.OrderByDescending(x => x.Metrics.TurnoverQuote).Take(3).ToList();
        Console.WriteLine($"  activity stage on top 3 over last 30m ({sample}):");
        foreach (var s in top3)
        {
            var m = await screener.MeasureActivityAsync(s, sample, 14, CancellationToken.None);
            var reasons = filter10.EvaluateActivity(m, thresholds);
            Console.WriteLine($"    {s.Symbol,-16} candles={m.Candles,4} medianRange%={m.MedianRangePct:0.0000} atr%={m.AtrPct:0.0000} coverage={m.Coverage:0.00} eligible={reasons.Count == 0}");
        }
    }
}

Console.WriteLine();
Console.WriteLine("DONE.");
