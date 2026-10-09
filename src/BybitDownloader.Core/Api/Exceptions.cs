namespace BybitDownloader.Core.Api;

/// <summary>Base for API failures. IsTransient tells the RetryPolicy whether a retry is sensible.</summary>
public class ApiException : Exception
{
    public bool IsTransient { get; }
    public TimeSpan? RetryAfter { get; }

    public ApiException(string message, bool isTransient, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }
}

public sealed class BybitApiException : ApiException
{
    public int RetCode { get; }
    public bool IsRateLimit => RetCode == 10006;

    // ASSUMPTION TO VERIFY against https://bybit-exchange.github.io/docs/v5/error :
    // 10000 server timeout, 10006 too many visits, 10016 server error, 10018 IP rate limit.
    private static readonly HashSet<int> TransientCodes = [10000, 10006, 10016, 10018];

    public BybitApiException(int retCode, string retMsg)
        : base($"Bybit error {retCode}: {retMsg}", TransientCodes.Contains(retCode))
    {
        RetCode = retCode;
    }
}

public sealed class RetryExhaustedException(int attempts, Exception last)
    : Exception($"Gave up after {attempts} attempt(s): {last.Message}", last)
{
    public int Attempts { get; } = attempts;
}
