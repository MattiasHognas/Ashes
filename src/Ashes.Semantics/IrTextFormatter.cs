using System.Globalization;
using System.Reflection;
using System.Text;

namespace Ashes.Semantics;

/// <summary>Which semantic IR a dump shows.</summary>
public enum IrDumpStage
{
    /// <summary>The IR as lowering emitted it, before any Ashes-level optimization.</summary>
    Lowered,

    /// <summary>The IR handed to code generation, after optimization.</summary>
    Final,
}

/// <summary>
/// Which IR dumps were requested, and for which functions.
/// </summary>
/// <param name="Stages">The requested stages, deduplicated by the set.</param>
/// <param name="FunctionFilter">
/// Restricts the dump to matching functions, matched the same way the explain reports match. Null
/// dumps everything.
/// </param>
public sealed record IrDumpRequest(
    IReadOnlySet<IrDumpStage> Stages,
    string? FunctionFilter = null)
{
    /// <summary>A request for no dump at all.</summary>
    public static IrDumpRequest None { get; } = new(new HashSet<IrDumpStage>());

    /// <summary>True when nothing was requested, so nothing should be produced or printed.</summary>
    public bool IsEmpty => Stages.Count == 0;

    /// <summary>Whether the given stage was requested.</summary>
    public bool Includes(IrDumpStage stage) => Stages.Contains(stage);

    /// <summary>
    /// Parses one <c>--emit-ir</c> value, in either the bare <c>final</c> or the filtered
    /// <c>final:Map.set</c> form. Returns false with a caller-formattable reason for an unknown stage.
    /// </summary>
    public static bool TryParseValue(
        string value,
        out IrDumpStage stage,
        out string? filter,
        out string? error)
    {
        stage = default;
        filter = null;
        error = null;

        string stageText = value;
        int separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator >= 0)
        {
            stageText = value[..separator];
            string rest = value[(separator + 1)..];
            filter = string.IsNullOrWhiteSpace(rest) ? null : rest;
        }

        switch (stageText.Trim().ToLowerInvariant())
        {
            case "lowered": stage = IrDumpStage.Lowered; return true;
            case "final": stage = IrDumpStage.Final; return true;
            default:
                error = $"Unknown IR stage '{stageText}'.";
                return false;
        }
    }

    /// <summary>The valid values, in pipeline order, for help text and error messages.</summary>
    public static IReadOnlyList<string> ValidValues { get; } = ["lowered", "final"];
}

/// <summary>
/// Renders semantic IR as text for reading and diffing.
/// </summary>
/// <remarks>
/// This is a debugging artifact, not a report: unlike the explain reports it makes no promise of a
/// stable shape, because its content is the instruction set itself and changes whenever an
/// instruction does. That is also why it is a separate option — folding it into <c>--explain</c>
/// would put churning text behind a contract that promises the opposite.
///
/// Instructions carry no ordinal. Diffing the lowered stage against the final one is the main reason
/// to read this at all, and an absolute index makes every line after an inserted or removed
/// instruction register as changed, burying the handful that actually differ. Labels anchor position
/// where position matters.
///
/// Fields are read reflectively rather than by a per-instruction printer. There are well over two
/// hundred instruction records; a hand-written printer would be a maintenance liability that silently
/// omits any field added later, while reflection stays correct by construction.
/// </remarks>
internal static class IrTextFormatter
{
    private const int OpcodeColumn = 22;

    internal static IReadOnlyList<string> Format(IrProgram program, IrDumpStage stage, string? filter)
    {
        var lines = new List<string>();
        string title = $"IR ({stage.ToString().ToLowerInvariant()})";
        lines.Add(title);
        lines.Add(new string('=', title.Length));
        lines.Add(string.Empty);
        AppendTraitEvidence(lines, program.TraitEvidence);

        var matched = 0;
        foreach (IrFunction function in program.Functions.Concat([program.EntryFunction]))
        {
            if (!IrFunctionSelector.Matches(function.Origin, function.Label, filter))
            {
                continue;
            }

            matched++;
            AppendFunction(lines, function);
        }

        if (matched == 0)
        {
            lines.Add("  (no functions matched)");
        }

        return lines;
    }

    private static void AppendTraitEvidence(List<string> lines, TraitEvidenceAnnotations evidence)
    {
        if (evidence.DictionaryParameters.Count == 0 && evidence.ResolvedImplementations.Count == 0)
        {
            return;
        }
        lines.Add("trait evidence");
        foreach (TraitDictionaryAbiAnnotation parameter in evidence.DictionaryParameters)
        {
            lines.Add($"  dictionary-parameter function={parameter.Function} source={parameter.FunctionSource}:{parameter.FunctionOffset.ToString(CultureInfo.InvariantCulture)} index={parameter.ParameterIndex.ToString(CultureInfo.InvariantCulture)} trait={parameter.Trait} methods=[{string.Join(",", parameter.Methods)}] supertraits=[{string.Join(",", parameter.Supertraits)}]");
        }
        foreach (TraitResolutionAnnotation resolution in evidence.ResolvedImplementations)
        {
            lines.Add($"  resolved requirement={resolution.Requirement} implementation={resolution.ImplementationModule} ({resolution.ImplementationSource}:{resolution.ImplementationOffset.ToString(CultureInfo.InvariantCulture)})");
        }
        lines.Add(string.Empty);
    }

    private static void AppendFunction(List<string> lines, IrFunction function)
    {
        lines.Add($"function {function.Label}{DescribeOrigin(function.Origin)}");
        lines.Add($"  locals={function.LocalCount.ToString(CultureInfo.InvariantCulture)} temps={function.TempCount.ToString(CultureInfo.InvariantCulture)}");

        foreach (IrInst instruction in function.Instructions)
        {
            // A label is a position, not an operation, so it sits out at the margin where a reader
            // scanning for control flow will find it.
            if (instruction is IrInst.Label label)
            {
                lines.Add($"  {label.Name}:");
                continue;
            }

            string opcode = instruction.GetType().Name.PadRight(OpcodeColumn);
            string operands = DescribeOperands(instruction);
            string location = instruction.Location is { } site
                ? $"   ({site.FilePath}:{site.Line.ToString(CultureInfo.InvariantCulture)}:{site.Column.ToString(CultureInfo.InvariantCulture)})"
                : string.Empty;
            lines.Add($"    {opcode}{operands}{location}".TrimEnd());
        }

        lines.Add(string.Empty);
    }

    private static string DescribeOrigin(IrFunctionOrigin? origin)
    {
        if (origin is null)
        {
            return string.Empty;
        }

        string source = origin.Source?.SourceName is { Length: > 0 } name ? $" from {name}" : string.Empty;
        return $"  [{origin.Kind}{source}]";
    }

    /// <summary>
    /// The instruction's operands, omitting anything left at its unset value. The IR spells "unset"
    /// as null, false, an empty string, or -1 for an optional slot or temp, and printing those would
    /// bury the operands that carry meaning. Zero is kept: temp and slot zero are real.
    /// </summary>
    private static string DescribeOperands(IrInst instruction)
    {
        var builder = new StringBuilder();
        foreach (PropertyInfo property in instruction.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (string.Equals(property.Name, "Location", StringComparison.Ordinal)
                || !property.CanRead
                || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            object? value = property.GetValue(instruction);
            if (IsUnset(value))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(property.Name).Append('=').Append(Render(value));
        }

        return builder.ToString();
    }

    private static bool IsUnset(object? value) => value switch
    {
        null => true,
        bool flag => !flag,
        int number => number == -1,
        string text => text.Length == 0,
        _ => false,
    };

    private static string Render(object? value) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        int number => number.ToString(CultureInfo.InvariantCulture),
        long number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        string text => text,
        // A collection's contents matter more than its type name; the counts alone identify shapes
        // like a suspend's saved-variable list without reproducing the whole table.
        System.Collections.ICollection collection => $"[{collection.Count.ToString(CultureInfo.InvariantCulture)}]",
        _ => value.ToString() ?? string.Empty,
    };
}
