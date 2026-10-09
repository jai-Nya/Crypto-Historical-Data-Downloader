using System.Text;
using BybitDownloader.Cli;

Console.OutputEncoding = Encoding.UTF8;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await CliApp.RunAsync(args, DateTimeOffset.UtcNow, Console.Out, Console.Error, cts.Token);
