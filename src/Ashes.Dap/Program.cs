namespace Ashes.Dap;

/// <summary>
/// Entry point for the Ashes DAP server. Communicates with IDE clients
/// (VS Code) over stdin/stdout using the Debug Adapter Protocol.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--help" or "-h")
        {
            Console.Error.WriteLine("Usage: ashes-dap");
            Console.Error.WriteLine("  Starts the Ashes Debug Adapter Protocol server on stdin/stdout.");
            Console.Error.WriteLine("  Intended to be launched by an IDE (VS Code) debug extension.");
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var server = new DapServer(Console.OpenStandardInput(), Console.OpenStandardOutput());
        try
        {
            await server.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }

        return 0;
    }
}
