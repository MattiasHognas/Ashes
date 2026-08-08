namespace Ashes.Semantics;

/// <summary>
/// Builds the reportable view of a compilation from the decisions lowering recorded and the final
/// semantic IR.
/// </summary>
/// <remarks>
/// The reference-count report reads the <see cref="IrProgram"/> handed to the backend, after
/// optimization and after the state-machine transform, because operation counts have to describe the
/// code that actually ships rather than what lowering first emitted. Everything else comes from
/// <see cref="CompilationDecisionSnapshot"/>: those decisions were captured where they were taken and
/// cannot be recovered from instructions.
/// </remarks>
internal static class IrExplainReporter
{
    internal static CompilationExplainReport Build(
        CompilationDecisionSnapshot snapshot,
        IrProgram finalIr,
        ExplainRequest request)
    {
        if (request.IsEmpty)
        {
            return CompilationExplainReport.Empty;
        }

        bool wantsOwnership = request.Includes(ExplainKind.Ownership) || request.Includes(ExplainKind.Memory);
        bool wantsRc = request.Includes(ExplainKind.Rc) || request.Includes(ExplainKind.Memory);
        bool wantsReuse = request.Includes(ExplainKind.Reuse) || request.Includes(ExplainKind.Memory);
        bool wantsRepresentation = request.Includes(ExplainKind.Memory);

        return new CompilationExplainReport(
            wantsOwnership ? BuildOwnership(snapshot, request.FunctionFilter) : [],
            wantsRc ? BuildRc(finalIr, request.FunctionFilter) : [],
            wantsReuse ? BuildReuse(snapshot, request.FunctionFilter) : [],
            wantsRepresentation ? BuildRepresentation(snapshot, request.FunctionFilter) : []);
    }

    private static IReadOnlyList<OwnershipFunctionReport> BuildOwnership(
        CompilationDecisionSnapshot snapshot,
        string? filter)
    {
        var reports = new List<OwnershipFunctionReport>();
        foreach (FunctionOwnershipRecord record in snapshot.FunctionOwnership)
        {
            if (!MatchesSource(record.Origin, record.Function, filter))
            {
                continue;
            }

            // Borrowed and consumed partition the parameter list, so one lookup settles the other.
            var borrowed = new HashSet<string>(record.BorrowedParameters, StringComparer.Ordinal);
            var unique = new HashSet<string>(record.UniqueParameters, StringComparer.Ordinal);

            var parameters = new List<OwnershipParameterReport>();
            foreach (string parameter in record.Parameters)
            {
                bool isBorrowed = borrowed.Contains(parameter);
                parameters.Add(new OwnershipParameterReport(
                    parameter,
                    isBorrowed ? ParameterOwnership.Borrowed : ParameterOwnership.Consumed,
                    unique.Contains(parameter),
                    // A parameter the analysis proved safe to move is exactly one it reports as
                    // consumed; borrowed means the caller keeps its reference.
                    MoveSafe: !isBorrowed));
            }

            reports.Add(new OwnershipFunctionReport(
                record.Function,
                record.Origin,
                parameters,
                record.ResultAliases,
                record.ResultFresh,
                record.ResultPoisoned,
                record.CapturedValues));
        }

        return reports;
    }

    private static IReadOnlyList<RcFunctionReport> BuildRc(IrProgram program, string? filter)
    {
        var reports = new List<RcFunctionReport>();
        foreach (IrFunction function in program.Functions.Concat([program.EntryFunction]))
        {
            if (!MatchesGenerated(function.Origin, function.Label, filter))
            {
                continue;
            }

            int dups = 0, drops = 0, uniqueness = 0, allocations = 0, reused = 0, tokens = 0, copies = 0;
            foreach (IrInst instruction in function.Instructions)
            {
                switch (instruction)
                {
                    case IrInst.RcDup: dups++; break;
                    case IrInst.RcDrop: drops++; break;
                    case IrInst.RcIsUnique: uniqueness++; break;
                    case IrInst.AllocReusing: reused++; break;
                    case IrInst.DropReuse: tokens++; break;
                    case IrInst.Alloc or IrInst.AllocAdt: allocations++; break;
                    case IrInst.CopyOutArena or IrInst.CopyOutList: copies++; break;
                    default: break;
                }
            }

            var report = new RcFunctionReport(
                function.Label,
                function.Origin,
                dups,
                drops,
                uniqueness,
                allocations,
                reused,
                tokens,
                copies);
            if (report.Total > 0)
            {
                reports.Add(report);
            }
        }

        return reports;
    }

    private static IReadOnlyList<ReuseFunctionReport> BuildReuse(
        CompilationDecisionSnapshot snapshot,
        string? filter)
    {
        var reports = new List<ReuseFunctionReport>();
        foreach (ReuseDecision decision in snapshot.ReuseDecisions)
        {
            if (!MatchesGenerated(decision.Function, decision.Function.GeneratedLabel, filter))
            {
                continue;
            }

            reports.Add(new ReuseFunctionReport(
                decision.Function.Source?.SourceName ?? decision.Function.GeneratedLabel,
                decision.Function,
                decision.Decision,
                decision.Outcome,
                decision.Reason,
                decision.Candidate?.SourceName,
                decision.Location));
        }

        return reports;
    }

    private static IReadOnlyList<RepresentationFunctionReport> BuildRepresentation(
        CompilationDecisionSnapshot snapshot,
        string? filter)
    {
        var byFunction = new Dictionary<string, (IrFunctionOrigin? Origin, Dictionary<ValuePlacementCategory, int> Counts)>(
            StringComparer.Ordinal);
        var order = new List<string>();

        foreach (ValuePlacementRecord record in snapshot.ValuePlacements)
        {
            string label = record.Function?.GeneratedLabel ?? "<program>";
            if (!MatchesGenerated(record.Function, label, filter))
            {
                continue;
            }

            if (!byFunction.TryGetValue(label, out var entry))
            {
                entry = (record.Function, new Dictionary<ValuePlacementCategory, int>());
                byFunction[label] = entry;
                order.Add(label);
            }

            entry.Counts[record.Placement] = entry.Counts.GetValueOrDefault(record.Placement) + 1;
        }

        return [.. order.Select(label => new RepresentationFunctionReport(
            label,
            byFunction[label].Origin,
            byFunction[label].Counts))];
    }

    /// <summary>
    /// Whether a source-level function is selected. A null filter selects everything; otherwise the
    /// selector matches the source name or the qualified name, case-insensitively and by substring, so
    /// <c>Map.set</c> and <c>set</c> both find the same function.
    /// </summary>
    private static bool MatchesSource(SourceFunctionOrigin? origin, string function, string? filter)
    {
        if (filter is null)
        {
            return true;
        }

        return Contains(function, filter)
            || Contains(origin?.SourceName, filter)
            || Contains(origin?.QualifiedName, filter);
    }

    /// <summary>
    /// Whether a generated function is selected. A generated function is reachable through the source
    /// function it came from as well as its own label, so a selector naming the source also selects
    /// the reuse specializations, droppers, and coroutines generated for it.
    /// </summary>
    private static bool MatchesGenerated(IrFunctionOrigin? origin, string label, string? filter)
    {
        if (filter is null)
        {
            return true;
        }

        return Contains(label, filter)
            || Contains(origin?.GeneratedLabel, filter)
            || Contains(origin?.Source?.SourceName, filter)
            || Contains(origin?.Source?.QualifiedName, filter)
            || Contains(origin?.ParentGeneratedLabel, filter);
    }

    private static bool Contains(string? candidate, string filter)
        => !string.IsNullOrEmpty(candidate)
            && candidate.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
