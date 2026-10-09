using BybitDownloader.Core.Api;
using BybitDownloader.Core.Models;

namespace BybitDownloader.Core.Tests;

public class ParserTests
{
    private const string InstrumentsJson = """
    {"retCode":0,"retMsg":"OK","result":{"category":"linear","list":[
      {"symbol":"BTCUSDT","contractType":"LinearPerpetual","status":"Trading","baseCoin":"BTC","quoteCoin":"USDT","settleCoin":"USDT","launchTime":"1585526400000","priceFilter":{"tickSize":"0.10"}},
      {"symbol":"BTC-27DEC24","contractType":"LinearFutures","status":"Trading","baseCoin":"BTC","quoteCoin":"USDT","settleCoin":"USDT","launchTime":"1700000000000","priceFilter":{"tickSize":"5"}},
      {"symbol":"NEWUSDT","contractType":"LinearPerpetual","status":"PreLaunch","baseCoin":"NEW","quoteCoin":"USDT","settleCoin":"USDT","launchTime":"","priceFilter":{"tickSize":"0.0001"}}
    ],"nextPageCursor":"page%3D2"},"retExtInfo":{},"time":1}
    """;

    [Fact]
    public void Instruments_parse_fields_and_cursor()
    {
        var page = BybitParser.ParseInstruments(InstrumentsJson, MarketCategory.Linear);
        Assert.Equal(3, page.Items.Count);
        Assert.Equal("page%3D2", page.NextCursor);
        var btc = page.Items[0];
        Assert.Equal(1_585_526_400_000, btc.LaunchTimeMs);
        Assert.Equal(0.10m, btc.TickSize);
        Assert.True(btc.IsPerpetual && btc.IsTrading);
    }

    [Fact]
    public void Futures_and_prelaunch_are_flagged_correctly()
    {
        var items = BybitParser.ParseInstruments(InstrumentsJson, MarketCategory.Linear).Items;
        Assert.False(items[1].IsPerpetual);
        Assert.True(items[2].IsPerpetual);
        Assert.False(items[2].IsTrading);
        Assert.Null(items[2].LaunchTimeMs); // empty string -> null, not 0
    }

    [Fact]
    public void Klines_are_returned_in_response_order_not_sorted()
    {
        var c = BybitParser.ParseKlines(Fx.KlineJson(Fx.B + 120_000, Fx.B + 60_000, Fx.B));
        Assert.Equal(3, c.Count);
        Assert.Equal(Fx.B + 120_000, c[0].TimestampMs);
        Assert.Equal(100.5m, c[0].Close);
        Assert.Equal(1005m, c[0].Turnover);
    }

    [Fact]
    public void Empty_kline_list_is_valid()
    {
        Assert.Empty(BybitParser.ParseKlines(Fx.KlineJson()));
    }

    [Theory]
    [InlineData(10006, true)]
    [InlineData(10016, true)]
    [InlineData(10001, false)]
    [InlineData(10001 + 100, false)]
    public void Non_zero_retcode_throws_with_transient_flag(int code, bool transient)
    {
        var ex = Assert.Throws<BybitApiException>(() => BybitParser.ParseKlines(Fx.Error(code, "x")));
        Assert.Equal(code, ex.RetCode);
        Assert.Equal(transient, ex.IsTransient);
        Assert.Equal(code == 10006, ex.IsRateLimit);
    }

    [Fact]
    public void Malformed_json_and_rows_throw_ApiException()
    {
        Assert.Throws<ApiException>(() => BybitParser.ParseKlines("<html>502</html>"));
        Assert.Throws<ApiException>(() => BybitParser.ParseKlines("""{"result":{}}"""));
        const string bad = """{"retCode":0,"result":{"list":[["1","a","2","3","4","5","6"]]}}""";
        Assert.Throws<ApiException>(() => BybitParser.ParseKlines(bad));
    }

    [Fact]
    public void Tickers_parse_and_empty_strings_become_null()
    {
        const string json = """
        {"retCode":0,"retMsg":"OK","result":{"category":"linear","list":[
          {"symbol":"BTCUSDT","lastPrice":"60000.5","highPrice24h":"61000","lowPrice24h":"59000","turnover24h":"1234567.89","volume24h":"20.5","openInterest":"100","openInterestValue":"6000000","bid1Price":"60000","ask1Price":"60001"},
          {"symbol":"ODDUSDT","lastPrice":"1","highPrice24h":"","lowPrice24h":"0","turnover24h":"0","volume24h":"0","openInterest":"0","openInterestValue":"","bid1Price":"","ask1Price":""}
        ]},"time":1}
        """;
        var t = BybitParser.ParseTickers(json);
        Assert.Equal(2, t.Count);
        Assert.Equal(1234567.89m, t[0].Turnover24h);
        Assert.Null(t[1].HighPrice24h);
        Assert.Null(t[1].OpenInterestValue);
        Assert.Equal(0m, t[1].LowPrice24h);
    }
}
