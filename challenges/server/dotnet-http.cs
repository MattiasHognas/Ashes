// Single-file .NET HTTP/1.1 server — a baseline to compare the Ashes HTTP server against. Deliberately
// a raw-socket server (not Kestrel/ASP.NET) so it matches what http_echo.ash does: parse a request off
// the socket and write a fixed 200 "ok". Concurrent async accept loop — the natural .NET idiom, so it
// shows the ceiling; the current Ashes serve() steps handlers cooperatively on one thread.
// Run via .NET 10 file-based apps:  dotnet run challenges/server/dotnet-http.cs [port]
using System.Net;
using System.Net.Sockets;
using System.Text;

int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 18081;
var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Console.WriteLine($"dotnet http listening on 127.0.0.1:{port}");

byte[] response = Encoding.ASCII.GetBytes(
    "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = HandleAsync(client);
}

async Task HandleAsync(TcpClient client)
{
    try
    {
        using (client)
        using (var stream = client.GetStream())
        {
            // Read until the end of the request head (blank line); the fixed handler ignores the body.
            var buffer = new byte[4096];
            var seen = new StringBuilder();
            while (!seen.ToString().Contains("\r\n\r\n"))
            {
                int read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (seen.Length > 65536) break;
            }
            await stream.WriteAsync(response);
        }
    }
    catch
    {
        // Best-effort; ignore per-connection errors (matches the Ashes handler isolating failures).
    }
}
