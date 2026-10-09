using BybitDownloader.Core.Models;
using BybitDownloader.Core.Planning;
using Parquet;
using Parquet.Schema;

namespace BybitDownloader.Storage;

/// <summary>Persists validated candle sets and resolves their file locations.</summary>
public interface ICandleStore
{
    string GetPath(MarketCategory category, string symbol, int year, int month);
    string GetPath(DownloadChunk chunk);
    bool Exists(string path);
    Task WriteAsync(string path, IReadOnlyList<Candle> candles, CancellationToken ct = default);
    Task<IReadOnlyList<Candle>> ReadAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// One Parquet file per symbol-month: {root}/{category}/{symbol}/{yyyy-MM}.parquet.
/// Writes are atomic: a uniquely-named temp file is fully written, flushed, then renamed over the target.
/// </summary>
public sealed class ParquetCandleStore : ICandleStore
{
    public const int DecimalPrecision = 38;
    public const int DecimalScale = 10;

    private static readonly ParquetSchema Schema = new(
        new DataField<long>("timestamp_ms"),
        new DecimalDataField("open", DecimalPrecision, DecimalScale),
        new DecimalDataField("high", DecimalPrecision, DecimalScale),
        new DecimalDataField("low", DecimalPrecision, DecimalScale),
        new DecimalDataField("close", DecimalPrecision, DecimalScale),
        new DecimalDataField("volume", DecimalPrecision, DecimalScale),
        new DecimalDataField("turnover", DecimalPrecision, DecimalScale));

    public ParquetCandleStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("Root directory is required.", nameof(rootDirectory));
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public string GetPath(MarketCategory category, string symbol, int year, int month) =>
        Path.Combine(RootDirectory, category.ToApiString(), Sanitize(symbol), $"{year:D4}-{month:D2}.parquet");

    public string GetPath(DownloadChunk chunk) =>
        GetPath(chunk.Category, chunk.Symbol, chunk.Year, chunk.Month);

    public bool Exists(string path) => File.Exists(path);

    public async Task WriteAsync(string path, IReadOnlyList<Candle> candles, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count == 0) throw new ArgumentException("Refusing to write an empty candle set.", nameof(candles));

        var dir = Path.GetDirectoryName(path)
                  ?? throw new ArgumentException($"Path has no directory: {path}", nameof(path));
        Directory.CreateDirectory(dir);

        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            var n = candles.Count;
            var ts = new long[n];
            var open = new decimal[n];
            var high = new decimal[n];
            var low = new decimal[n];
            var close = new decimal[n];
            var volume = new decimal[n];
            var turnover = new decimal[n];
            for (var i = 0; i < n; i++)
            {
                var c = candles[i];
                ts[i] = c.TimestampMs;
                open[i] = c.Open;
                high[i] = c.High;
                low[i] = c.Low;
                close[i] = c.Close;
                volume[i] = c.Volume;
                turnover[i] = c.Turnover;
            }

            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var options = new ParquetOptions { CompressionMethod = CompressionMethod.Snappy };
                await using (var writer = await ParquetWriter.CreateAsync(Schema, fs, options, false, ct)
                                               .ConfigureAwait(false))
                {
                    using var rg = writer.CreateRowGroup();
                    await rg.WriteAsync<long>(Schema.DataFields[0], ts.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
                    await rg.WriteAsync<decimal>(Schema.DataFields[1], open.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
                    await rg.WriteAsync<decimal>(Schema.DataFields[2], high.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
                    await rg.WriteAsync<decimal>(Schema.DataFields[3], low.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
                    await rg.WriteAsync<decimal>(Schema.DataFields[4], close.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
                    await rg.WriteAsync<decimal>(Schema.DataFields[5], volume.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
                    await rg.WriteAsync<decimal>(Schema.DataFields[6], turnover.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
                }
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public async Task<IReadOnlyList<Candle>> ReadAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var reader = await ParquetReader.CreateAsync(fs, cancellationToken: ct).ConfigureAwait(false);

        var result = new List<Candle>();
        var fields = reader.Schema.DataFields;
        for (var g = 0; g < reader.RowGroupCount; g++)
        {
            ct.ThrowIfCancellationRequested();
            using var rg = reader.OpenRowGroupReader(g);
            var n = checked((int)rg.RowCount);
            var ts = new long[n];
            var open = new decimal[n];
            var high = new decimal[n];
            var low = new decimal[n];
            var close = new decimal[n];
            var volume = new decimal[n];
            var turnover = new decimal[n];
            await rg.ReadAsync<long>(fields[0], ts.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
            await rg.ReadAsync<decimal>(fields[1], open.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
            await rg.ReadAsync<decimal>(fields[2], high.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
            await rg.ReadAsync<decimal>(fields[3], low.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
            await rg.ReadAsync<decimal>(fields[4], close.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
            await rg.ReadAsync<decimal>(fields[5], volume.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
            await rg.ReadAsync<decimal>(fields[6], turnover.AsMemory(), cancellationToken: ct).ConfigureAwait(false);
            for (var i = 0; i < n; i++)
                result.Add(new Candle(ts[i], open[i], high[i], low[i], close[i], volume[i], turnover[i]));
        }
        return result;
    }

    private static string Sanitize(string symbol)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = symbol.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
