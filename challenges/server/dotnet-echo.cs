// Single-file .NET TCP echo server — a baseline to compare the Ashes server against.
// Run via .NET 10 file-based apps:  dotnet run challenges/server/dotnet-echo.cs [port]
// Concurrent async accept loop (one receive + echo + close per connection) — the natural .NET idiom,
// so it shows the ceiling. Note: the current Ashes serve() is sequential, so this baseline is
// deliberately more concurrent; the gap is the headroom the multi-reactor milestone targets.
using System.Net;
using System.Net.Sockets;

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 18080;
var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Console.WriteLine($"dotnet echo listening on 127.0.0.1:{port}");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = HandleAsync(client);
}

static async Task HandleAsync(TcpClient client)
{
    try
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var buffer = new byte[4096];
            int read = await stream.ReadAsync(buffer);
            if (read > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read));
            }
        }
    }
    catch
    {
        // Best-effort echo; ignore per-connection errors (matches the Ashes handler isolating failures).
    }
}
