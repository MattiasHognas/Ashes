using System.Globalization;
using System.Text.RegularExpressions;

namespace Ashes.Dap;

/// <summary>
/// Parses GDB/LLDB Machine Interface (MI) result records into
/// structured data usable by the DAP server.
/// </summary>
public static partial class MiResponseParser
{
    public sealed record MiVariableObject(string Name, string Value, string Type);

    /// <summary>
    /// Parses a <c>-stack-list-frames</c> result record into DAP stack frames.
    /// <para>
    /// Example MI output:
    /// <c>^done,stack=[frame={level="0",addr="0x401000",func="main",file="main.ash",fullname="/p/main.ash",line="5"},...]</c>
    /// </para>
    /// </summary>
    public static DapStackFrame[] ParseStackFrames(string miResponse)
    {
        var frames = new List<DapStackFrame>();

        foreach (Match m in FrameRegex().Matches(miResponse))
        {
            var body = m.Groups[1].Value;
            var level = ExtractField(body, "level");
            var func = ExtractField(body, "func");
            var file = ExtractField(body, "file");
            var fullname = ExtractField(body, "fullname");
            var lineStr = ExtractField(body, "line");

            int.TryParse(level, CultureInfo.InvariantCulture, out var id);
            int.TryParse(lineStr, CultureInfo.InvariantCulture, out var line);

            var source = (file ?? fullname) is not null
                ? new DapSource
                {
                    Name = file,
                    Path = fullname ?? file,
                }
                : null;

            frames.Add(new DapStackFrame
            {
                Id = id,
                Name = func ?? $"frame {level}",
                Source = source,
                Line = line,
                Column = 0,
            });
        }

        return [.. frames];
    }

    /// <summary>
    /// Parses a <c>-stack-list-variables 1</c> (or <c>-stack-list-locals 1</c>)
    /// result record into DAP variables. Function arguments carry an extra
    /// <c>arg="1"</c> field between name and value.
    /// <para>
    /// Example MI output:
    /// <c>^done,variables=[{name="n",arg="1",value="10"},{name="x",value="42"}]</c>
    /// </para>
    /// </summary>
    public static DapVariable[] ParseVariables(string miResponse)
    {
        var variables = new List<DapVariable>();

        foreach (Match m in LocalRegex().Matches(miResponse))
        {
            var body = m.Groups[1].Value;
            var name = ExtractField(body, "name");
            var value = ExtractField(body, "value");

            if (name is not null)
            {
                variables.Add(new DapVariable
                {
                    Name = name,
                    Value = value ?? "",
                    VariablesReference = 0,
                });
            }
        }

        return [.. variables];
    }

    public static MiVariableObject? ParseVariableObject(string miResponse)
    {
        if (string.IsNullOrWhiteSpace(miResponse))
        {
            return null;
        }

        var name = ExtractField(miResponse, "name");
        var value = ExtractField(miResponse, "value");
        var type = ExtractField(miResponse, "type");
        if (string.IsNullOrWhiteSpace(name) || value is null || string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        return new MiVariableObject(name, value, type);
    }

    public static string? ParseEvaluateExpressionValue(string miResponse)
    {
        if (string.IsNullOrWhiteSpace(miResponse))
        {
            return null;
        }

        return ExtractField(miResponse, "value");
    }

    private static string? ExtractField(string text, string fieldName)
    {
        var match = Regex.Match(text, $@"{fieldName}=""([^""]*)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Matches each <c>frame={...}</c> block in a stack-list-frames result.</summary>
    [GeneratedRegex(@"frame=\{([^}]+)\}")]
    private static partial Regex FrameRegex();

    /// <summary>Matches each <c>{name="...",[arg="...",]value="..."}</c> block in a variables/locals result.</summary>
    [GeneratedRegex(@"\{(name=""[^""]*"",(?:arg=""[^""]*"",)?value=""[^""]*"")\}")]
    private static partial Regex LocalRegex();
}
