using System.Text;
using System.Text.Json;

namespace Ashes.Dap;

/// <summary>
/// Reads and writes DAP messages using the standard Content-Length header framing
/// over two streams (typically stdin/stdout).
/// </summary>
public sealed class DapTransport
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly object _writeLock = new();
    private int _seq;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public DapTransport(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public async Task<DapRequest?> ReadRequestAsync(CancellationToken ct = default)
    {
        var json = await ReadMessageAsync(ct);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DapRequest>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SendResponse(DapRequest request, bool success, object? body = null, string? message = null)
    {
        var response = new DapResponse
        {
            Seq = NextSeq(),
            RequestSeq = request.Seq,
            Success = success,
            Command = request.Command,
            Body = body,
            Message = message,
        };
        WriteMessage(response);
    }

    public void SendEvent(string eventName, object? body = null)
    {
        var evt = new DapEvent
        {
            Seq = NextSeq(),
            Event = eventName,
            Body = body,
        };
        WriteMessage(evt);
    }

    private int NextSeq() => Interlocked.Increment(ref _seq);

    private void WriteMessage(DapMessage message)
    {
        var json = JsonSerializer.Serialize(message, message.GetType(), SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {bytes.Length}\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        lock (_writeLock)
        {
            _output.Write(headerBytes);
            _output.Write(bytes);
            _output.Flush();
        }
    }

    private async Task<string?> ReadMessageAsync(CancellationToken ct)
    {
        var headerLine = await ReadLineAsync(ct);
        if (headerLine is null)
        {
            return null;
        }

        int contentLength = 0;
        while (!string.IsNullOrWhiteSpace(headerLine))
        {
            if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var valueStr = headerLine["Content-Length:".Length..].Trim();
                contentLength = int.Parse(valueStr, System.Globalization.CultureInfo.InvariantCulture);
            }

            headerLine = await ReadLineAsync(ct);
        }

        if (contentLength == 0)
        {
            return null;
        }

        var buffer = new byte[contentLength];
        int totalRead = 0;
        while (totalRead < contentLength)
        {
            int read = await _input.ReadAsync(buffer.AsMemory(totalRead, contentLength - totalRead), ct);
            if (read == 0)
            {
                return null;
            }

            totalRead += read;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            int read = await _input.ReadAsync(buf, ct);
            if (read == 0)
            {
                return sb.Length > 0 ? sb.ToString() : null;
            }

            char c = (char)buf[0];
            if (c == '\n')
            {
                // Strip trailing \r
                if (sb.Length > 0 && sb[^1] == '\r')
                {
                    sb.Length--;
                }

                return sb.ToString();
            }

            sb.Append(c);
        }
    }
}
