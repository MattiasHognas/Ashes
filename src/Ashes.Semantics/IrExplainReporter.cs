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
        bool wantsTraits = request.Includes(ExplainKind.Traits) || request.Includes(ExplainKind.Memory);
        bool wantsAuthority = request.Includes(ExplainKind.Authority);
        bool wantsConcurrency = request.Includes(ExplainKind.Concurrency);

        return new CompilationExplainReport(
            wantsOwnership ? BuildOwnership(snapshot, request.FunctionFilter) : [],
            wantsRc ? BuildRc(finalIr, request.FunctionFilter) : [],
            wantsReuse ? BuildReuse(snapshot, request.FunctionFilter) : [],
            wantsRepresentation ? BuildRepresentation(snapshot, request.FunctionFilter) : [],
            wantsTraits ? finalIr.TraitEvidence : TraitEvidenceAnnotations.Empty)
        {
            ExternalResources = wantsOwnership
                ? FilterExternalResources(snapshot.ExternalResources, request.FunctionFilter)
                : [],
            Authority = wantsAuthority
                ? FilterPublicAuthority(snapshot.PublicAuthority, request.FunctionFilter)
                : [],
            ExternalAuthority = wantsAuthority
                ? FilterExternalAuthority(snapshot.ExternalAuthority, request.FunctionFilter)
                : [],
            Concurrency = wantsConcurrency
                ? BuildConcurrency(finalIr, request.FunctionFilter)
                : [],
        };
    }

    private static IReadOnlyList<ConcurrencyFunctionReport> BuildConcurrency(IrProgram program, string? filter)
    {
        var reports = new List<ConcurrencyFunctionReport>();
        foreach (IrFunction function in new[] { program.EntryFunction }.Concat(program.Functions))
        {
            if (!IrFunctionSelector.Matches(function.Origin, function.Label, filter))
            {
                continue;
            }

            int scopes = function.Instructions.Count(instruction => instruction is IrInst.CreateScopedTask);
            int forks = function.Instructions.Count(instruction => instruction is IrInst.ForkScopedTask);
            int joins = function.Instructions.Count(instruction => instruction is IrInst.JoinScopedTask);
            int spawns = function.Instructions.Count(instruction => instruction is IrInst.SpawnTask);
            if (scopes + forks + joins + spawns > 0)
            {
                reports.Add(new ConcurrencyFunctionReport(function.Label, function.Origin, scopes, forks, joins, spawns));
            }
        }

        return reports;
    }

    private static IReadOnlyList<PublicAuthorityRecord> FilterPublicAuthority(
        IReadOnlyList<PublicAuthorityRecord> records,
        string? filter) => filter is null
            ? records
            : [.. records.Where(record => record.Binding.Contains(filter, StringComparison.OrdinalIgnoreCase))];

    private static IReadOnlyList<ExternalAuthorityRecord> FilterExternalAuthority(
        IReadOnlyList<ExternalAuthorityRecord> records,
        string? filter) => filter is null
            ? records
            : [.. records.Where(record => record.Function.Contains(filter, StringComparison.OrdinalIgnoreCase))];

    private static IReadOnlyList<ExternalResourceOwnershipRecord> FilterExternalResources(
        IReadOnlyList<ExternalResourceOwnershipRecord> resources,
        string? filter)
    {
        if (filter is null)
        {
            return resources;
        }

        return [.. resources.Where(resource =>
            string.Equals(resource.Destructor, filter, StringComparison.Ordinal)
            || resource.Parameters.Any(parameter =>
                string.Equals(parameter.Function, filter, StringComparison.Ordinal)))];
    }

    private static IReadOnlyList<OwnershipFunctionReport> BuildOwnership(
        CompilationDecisionSnapshot snapshot,
        string? filter)
    {
        var reports = new List<OwnershipFunctionReport>();
        foreach (FunctionOwnershipRecord record in snapshot.FunctionOwnership)
        {
            if (!IrFunctionSelector.MatchesSource(record.Origin, record.Function, filter))
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
            if (!IrFunctionSelector.Matches(function.Origin, function.Label, filter))
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
            if (!IrFunctionSelector.Matches(decision.Function, decision.Function.GeneratedLabel, filter))
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
            if (!IrFunctionSelector.Matches(record.Function, label, filter))
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

}
