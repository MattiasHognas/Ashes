namespace Ashes.Cli.Registry;

/// <summary>Parsed command arguments: <c>--name value</c> options, boolean <c>--flag</c>s, and positionals.</summary>
internal sealed record ScannedArgs(
    IReadOnlyList<string> Positionals,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlySet<string> Flags)
{
    public string? Value(string name) => Values.TryGetValue(name, out var v) ? v : null;

    public bool Flag(string name) => Flags.Contains(name);
}

/// <summary>A tiny argument scanner for the registry verbs. Boolean flags are a fixed set; every other
/// <c>--option</c> consumes the following token as its value.</summary>
internal static class ArgScanner
{
    private static readonly HashSet<string> BooleanFlags = new(StringComparer.Ordinal)
    {
        "undo", "help", "dev", "frozen", "offline",
    };

    public static ScannedArgs Parse(string[] args)
    {
        var positionals = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h")
            {
                flags.Add("help");
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }

            var name = arg[2..];
            if (BooleanFlags.Contains(name))
            {
                flags.Add(name);
            }
            else if (i + 1 < args.Length)
            {
                values[name] = args[++i];
            }
            else
            {
                throw new CliUsageException($"Option --{name} requires a value.");
            }
        }

        return new ScannedArgs(positionals, values, flags);
    }
}
