using System.Globalization;
using System.Text;

namespace Ashes.Semantics;

/// <summary>
/// Renders a <see cref="CompilationExplainReport"/> as text. Formatting lives here rather than in the
/// passes that produce the facts, so no semantic pass writes prose or reaches a console.
/// </summary>
/// <remarks>
/// Output is deterministic and snapshot-stable: section and function order follow the report's own
/// order, which the snapshot fixes by ordinal; numbers use the invariant culture; nothing carries a
/// timestamp, an address, or a value read from a hash-ordered collection.
/// </remarks>
internal static class ExplainReportFormatter
{
    private const int CountColumn = 26;

    internal static IReadOnlyList<string> Format(CompilationExplainReport report, ExplainRequest request)
    {
        var lines = new List<string>();
        if (request.Includes(ExplainKind.Ownership))
        {
            AppendOwnership(lines, report.Ownership, report.ExternalResources);
        }

        if (request.Includes(ExplainKind.Rc))
        {
            AppendRc(lines, report.Rc);
        }

        if (request.Includes(ExplainKind.Reuse))
        {
            AppendReuse(lines, report.Reuse);
        }

        if (request.Includes(ExplainKind.Traits))
        {
            AppendTraits(lines, report.TraitEvidence);
        }

        if (request.Includes(ExplainKind.Memory))
        {
            AppendMemory(lines, report);
        }

        return lines;
    }

    private static void AppendTraits(List<string> lines, TraitEvidenceAnnotations evidence)
    {
        Heading(lines, "Trait evidence report");
        if (evidence.DictionaryParameters.Count == 0 && evidence.ResolvedImplementations.Count == 0)
        {
            lines.Add("  (no trait evidence)");
            return;
        }

        foreach (TraitDictionaryAbiAnnotation parameter in evidence.DictionaryParameters)
        {
            lines.Add($"Function: {parameter.Function} ({parameter.FunctionSource}:{parameter.FunctionOffset.ToString(CultureInfo.InvariantCulture)})");
            lines.Add($"  dictionary parameter {parameter.ParameterIndex.ToString(CultureInfo.InvariantCulture)}: {parameter.Trait}");
            lines.Add($"    methods: {string.Join(", ", parameter.Methods)}");
            lines.Add($"    supertraits: {(parameter.Supertraits.Count == 0 ? "(none)" : string.Join(", ", parameter.Supertraits))}");
        }
        foreach (TraitResolutionAnnotation resolution in evidence.ResolvedImplementations)
        {
            lines.Add($"Resolved: {resolution.Requirement}");
            lines.Add($"  implementation: {resolution.ImplementationModule} ({resolution.ImplementationSource}:{resolution.ImplementationOffset.ToString(CultureInfo.InvariantCulture)})");
        }
        lines.Add(string.Empty);
    }

    private static void AppendOwnership(
        List<string> lines,
        IReadOnlyList<OwnershipFunctionReport> reports,
        IReadOnlyList<ExternalResourceOwnershipRecord> resources)
    {
        Heading(lines, "Ownership report");
        if (reports.Count == 0 && resources.Count == 0)
        {
            lines.Add("  (no functions matched)");
            return;
        }

        AppendExternalResources(lines, resources);

        foreach (OwnershipFunctionReport report in reports)
        {
            lines.Add($"Function: {Describe(report.Origin, report.Function)}");
            lines.Add("  Parameters");
            if (report.Parameters.Count == 0)
            {
                lines.Add("    (none)");
            }

            foreach (OwnershipParameterReport parameter in report.Parameters)
            {
                lines.Add($"    {parameter.Name}");
                lines.Add($"      ownership: {parameter.Ownership.ToString().ToLowerInvariant()}");
                lines.Add($"      move-safe: {YesNo(parameter.MoveSafe)}");
                lines.Add($"      unique:    {YesNo(parameter.Unique)}");
            }

            lines.Add("  Result");
            lines.Add($"    fresh:    {YesNo(report.ResultFresh)}");
            lines.Add($"    poisoned: {YesNo(report.ResultPoisoned)}");
            if (report.ResultAliases.Count == 0)
            {
                lines.Add("    aliases:  (none)");
            }
            else
            {
                lines.Add("    aliases:");
                foreach (string alias in report.ResultAliases)
                {
                    lines.Add($"      - {alias}");
                }
            }

            if (report.CapturedValues.Count > 0)
            {
                lines.Add("  Captured");
                foreach (string captured in report.CapturedValues)
                {
                    lines.Add($"    - {captured}");
                }
            }

            lines.Add(string.Empty);
        }
    }

    private static void AppendExternalResources(
        List<string> lines,
        IReadOnlyList<ExternalResourceOwnershipRecord> resources)
    {
        foreach (ExternalResourceOwnershipRecord resource in resources)
        {
            lines.Add($"External resource: {resource.TypeName}");
            lines.Add($"  destructor: {resource.Destructor}");
            foreach (ExternalResourceParameterRecord parameter in resource.Parameters)
            {
                lines.Add($"  {parameter.Function} parameter #{parameter.ParameterIndex + 1}: {parameter.Ownership.ToString().ToLowerInvariant()}");
            }
            lines.Add(string.Empty);
        }
    }

    private static void AppendRc(List<string> lines, IReadOnlyList<RcFunctionReport> reports)
    {
        Heading(lines, "RC report");
        if (reports.Count == 0)
        {
            lines.Add("  (no functions matched)");
            return;
        }

        foreach (RcFunctionReport report in reports)
        {
            lines.Add($"Function: {Describe(report.Origin, report.Label)}");
            lines.Add("  Operations");
            Counted(lines, "dup", report.Dups);
            Counted(lines, "drop", report.Drops);
            Counted(lines, "uniqueness checks", report.UniquenessChecks);
            Counted(lines, "allocations", report.Allocations);
            Counted(lines, "reused allocations", report.ReusedAllocations);
            Counted(lines, "reuse tokens", report.ReuseTokens);
            Counted(lines, "copies", report.Copies);
            lines.Add(string.Empty);
        }
    }

    private static void AppendReuse(List<string> lines, IReadOnlyList<ReuseFunctionReport> reports)
    {
        Heading(lines, "Reuse report");
        if (reports.Count == 0)
        {
            lines.Add("  (no functions matched)");
            return;
        }

        string? currentFunction = null;
        foreach (ReuseFunctionReport report in reports)
        {
            string function = Describe(report.Origin, report.Function);
            if (!string.Equals(function, currentFunction, StringComparison.Ordinal))
            {
                if (currentFunction is not null)
                {
                    lines.Add(string.Empty);
                }

                lines.Add($"Function: {function}");
                currentFunction = function;
            }

            string candidate = report.Candidate is null ? string.Empty : $" [{report.Candidate}]";
            string location = report.Location is { } site ? $" ({Describe(site)})" : string.Empty;
            lines.Add($"  {Spaced(report.Decision.ToString())}: {Spaced(report.Outcome.ToString())}{candidate}");
            lines.Add($"    reason: {Spaced(report.Reason.ToString())}{location}");
        }

        lines.Add(string.Empty);
    }

    private static void AppendMemory(List<string> lines, CompilationExplainReport report)
    {
        Heading(lines, "Memory report");
        if (report.Ownership.Count == 0 && report.Rc.Count == 0 && report.Representation.Count == 0)
        {
            lines.Add("  (no functions matched)");
            return;
        }

        // Correlated by source function: ownership is a source-level fact while reference counting and
        // representation are per generated function, so the generated ones are gathered under the
        // source function that produced them.
        foreach (OwnershipFunctionReport ownership in report.Ownership)
        {
            lines.Add($"Function: {Describe(ownership.Origin, ownership.Function)}");
            lines.Add("  ownership");
            foreach (OwnershipParameterReport parameter in ownership.Parameters)
            {
                lines.Add($"    {parameter.Name}: {parameter.Ownership.ToString().ToLowerInvariant()}, move-safe {YesNo(parameter.MoveSafe)}");
            }

            lines.Add($"    result fresh: {YesNo(ownership.ResultFresh)}");

            foreach (RcFunctionReport rc in report.Rc.Where(rc => BelongsTo(rc.Origin, ownership.Origin)))
            {
                lines.Add($"  perceus [{rc.Label}]");
                Counted(lines, "dup", rc.Dups, indent: 4);
                Counted(lines, "drop", rc.Drops, indent: 4);
                Counted(lines, "uniqueness checks", rc.UniquenessChecks, indent: 4);
            }

            foreach (ReuseFunctionReport reuse in report.Reuse.Where(reuse => BelongsTo(reuse.Origin, ownership.Origin)))
            {
                lines.Add($"  reuse: {Spaced(reuse.Decision.ToString())} {Spaced(reuse.Outcome.ToString())}");
            }

            foreach (RepresentationFunctionReport representation in report.Representation
                .Where(representation => BelongsTo(representation.Origin, ownership.Origin)))
            {
                lines.Add($"  representation [{representation.Label}]");
                foreach (ValuePlacementCategory category in Enum.GetValues<ValuePlacementCategory>())
                {
                    int count = representation.Placements.GetValueOrDefault(category);
                    if (count > 0)
                    {
                        Counted(lines, Spaced(category.ToString()), count, indent: 4);
                    }
                }
            }

            lines.Add(string.Empty);
        }

        if (report.TraitEvidence.DictionaryParameters.Count > 0
            || report.TraitEvidence.ResolvedImplementations.Count > 0)
        {
            AppendTraits(lines, report.TraitEvidence);
        }
    }

    /// <summary>Whether a generated function was produced for the given source function.</summary>
    private static bool BelongsTo(IrFunctionOrigin? generated, SourceFunctionOrigin source)
        => generated?.Source is { } origin && origin == source;

    private static void Heading(List<string> lines, string title)
    {
        if (lines.Count > 0)
        {
            lines.Add(string.Empty);
        }

        lines.Add(title);
        lines.Add(new string('=', title.Length));
        lines.Add(string.Empty);
    }

    private static void Counted(List<string> lines, string label, int value, int indent = 4)
    {
        string padded = (label + ":").PadRight(CountColumn - indent);
        lines.Add($"{new string(' ', indent)}{padded}{value.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string Describe(SourceFunctionOrigin origin, string fallback)
        => string.IsNullOrEmpty(origin.QualifiedName) ? fallback : origin.QualifiedName;

    private static string Describe(IrFunctionOrigin? origin, string fallback)
    {
        if (origin?.Source is { } source && !string.IsNullOrEmpty(source.SourceName))
        {
            return string.Equals(origin.GeneratedLabel, fallback, StringComparison.Ordinal)
                && origin.Kind == IrFunctionOriginKind.SourceFunction
                    ? source.SourceName
                    : $"{source.SourceName} [{fallback}]";
        }

        return fallback;
    }

    private static string Describe(SourceLocation location)
        => $"{location.FilePath}:{location.Line.ToString(CultureInfo.InvariantCulture)}:{location.Column.ToString(CultureInfo.InvariantCulture)}";

    private static string YesNo(bool value) => value ? "yes" : "no";

    /// <summary>
    /// Turns a PascalCase enum name into spaced lower case, so reason codes read as prose without the
    /// enum itself carrying formatted text.
    /// </summary>
    private static string Spaced(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}
