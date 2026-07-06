// Single-file .NET load generator for the server benchmarks. The SAME driver is used against both
// the Ashes server and its .NET baseline in each benchmark, so the comparison isolates the server
// (same functionality, same client) rather than mixing an Ashes client with a .NET server. It is fast
// (does the concurrency and timing itself), so the server is the bottleneck, not the driver.
//
// Two modes, selected by the 4th argument:
//   tcp  (default) — one connection per request: connect -> send "ping" -> read the echo back -> close
//   http           — one connection per request: connect -> send a GET -> read the full response -> close
//
// Usage: dotnet run loadgen.cs [requests] [concurrency] [port] [tcp|http]
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

int requests = args.Length > 0 ? int.Parse(args[0]) : 20000;
int concurrency = args.Length > 1 ? int.Parse(args[1]) : 1;
int port = args.Length > 2 ? int.Parse(args[2]) : 18080;
string mode = args.Length > 3 ? args[3].ToLowerInvariant() : "tcp";
bool http = mode == "http";
byte[] payload = http
    ? Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
    : Encoding.UTF8.GetBytes("ping");

// Wait for the server to accept, then warm up.
var upDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
bool up = false;
while (DateTime.UtcNow < upDeadline)
{
    if (await OneRequest() is not null) { up = true; break; }
    await Task.Delay(100);
}
if (!up)
{
    Console.WriteLine($"  server on 127.0.0.1:{port} did not come up");
    return;
}
for (int i = 0; i < 200; i++) await OneRequest();

var latencies = new ConcurrentBag<double>();
int errors = 0;
int perWorker = Math.Max(1, requests / concurrency);
int total = perWorker * concurrency;

var sw = Stopwatch.StartNew();
await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
{
    for (int i = 0; i < perWorker; i++)
    {
        var lat = await OneRequest();
        if (lat is double ms) latencies.Add(ms);
        else Interlocked.Increment(ref errors);
    }
})));
sw.Stop();

var xs = latencies.ToArray();
Array.Sort(xs);
double Pct(int p) => xs.Length == 0 ? 0 : xs[Math.Min(xs.Length - 1, (int)Math.Round(p / 100.0 * (xs.Length - 1)))];
double rps = sw.Elapsed.TotalSeconds > 0 ? total / sw.Elapsed.TotalSeconds : 0;
Console.WriteLine($"  requests {total,-7} conc {concurrency,-4} errors {errors,-4} "
    + $"throughput {rps,10:N0} req/s   p50 {Pct(50),6:F3}  p99 {Pct(99),6:F3}  max {(xs.Length > 0 ? xs[^1] : 0),6:F3} ms");

async Task<double?> OneRequest()
{
    var t0 = Stopwatch.GetTimestamp();
    try
    {
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5));
        using var s = c.GetStream();
        await s.WriteAsync(payload);
        if (http)
        {
            // Read the whole response until the server closes the connection (Connection: close).
            var buf = new byte[4096];
            int gotTotal = 0;
            while (true)
            {
                int r = await s.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                if (r == 0) break;
                gotTotal += r;
            }
            return gotTotal > 0 ? Stopwatch.GetElapsedTime(t0).TotalMilliseconds : null;
        }
        else
        {
            var buf = new byte[payload.Length];
            int got = 0;
            while (got < buf.Length)
            {
                int r = await s.ReadAsync(buf.AsMemory(got)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                if (r == 0) break;
                got += r;
            }
            return Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }
    catch
    {
        return null;
    }
}
