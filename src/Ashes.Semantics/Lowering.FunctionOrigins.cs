using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private sealed record IrFunctionOriginSeed(
        IrFunctionOriginKind Kind,
        SourceFunctionOrigin? Source = null,
        string? ParentGeneratedLabel = null,
        CompilerFunctionOwner? CompilerOwner = null,
        string? StableDiscriminator = null,
        SourceLocation? GenerationLocation = null);

    private IrFunctionOrigin? _activeFunctionOrigin;
    private readonly Dictionary<string, IrFunctionOrigin> _irFunctionOriginsByLabel =
        new(StringComparer.Ordinal);
    private readonly Dictionary<SourceFunctionOrigin, string> _primaryFunctionLabelBySource = new();

    private static IrFunctionOrigin CreateProgramEntryOrigin()
        => new(
            "_start_main",
            IrFunctionOriginKind.ProgramEntry,
            CompilerOwner: new CompilerFunctionOwner(
                CompilerFunctionOwnerKind.Program,
                "program entry"));

    private IrFunctionOrigin PrepareFunctionOrigins(Expr expression)
    {
        if (!_maAnalyzed)
        {
            DiscoverSourceFunctionOrigins(expression, enclosingSource: null);
        }

        IrFunctionOrigin entryOrigin = CreateProgramEntryOrigin();
        _activeFunctionOrigin = entryOrigin;
        _ownershipPlacementContext = new OwnershipPlacementContext(
            _maAnalyzed
                ? _maEntryMayExecuteUnderLiveHandlerPost
                : ExpressionContainsHandleForOwner(expression, owner: null),
            // The entry expression itself runs outside every coroutine: the async body it may create
            // becomes its own function, and everything that body reaches is marked separately.
            MayExecuteInsideCoroutine: false,
            // Likewise, the entry expression is never itself a parallel worker callback — a
            // Parallel.reduce/map call it makes spawns its own, separately-marked functions.
            MayExecuteAsParallelWorker: false);
        _ownershipPlacementByFunctionLabel[entryOrigin.GeneratedLabel] =
            _ownershipPlacementContext;
        return entryOrigin;
    }

    private void DiscoverSourceFunctionOrigins(
        Expr expression,
        SourceFunctionOrigin? enclosingSource)
    {
        switch (expression)
        {
            case Expr.Let let:
                {
                    SourceFunctionOrigin? declared = DiscoverSourceFunctionOrigin(
                        let.Name,
                        let.Value,
                        AstSpans.GetLetNameOrDefault(let),
                        enclosingSource);
                    DiscoverSourceFunctionOrigins(let.Value, declared ?? enclosingSource);
                    DiscoverSourceFunctionOrigins(let.Body, enclosingSource);
                    return;
                }
            case Expr.LetRecursive let:
                {
                    SourceFunctionOrigin? declared = DiscoverSourceFunctionOrigin(
                        let.Name,
                        let.Value,
                        AstSpans.GetLetRecursiveNameOrDefault(let),
                        enclosingSource);
                    DiscoverSourceFunctionOrigins(let.Value, declared ?? enclosingSource);
                    DiscoverSourceFunctionOrigins(let.Body, enclosingSource);
                    return;
                }
            case Expr.LetResult let:
                {
                    SourceFunctionOrigin? declared = DiscoverSourceFunctionOrigin(
                        let.Name,
                        let.Value,
                        AstSpans.GetLetResultNameOrDefault(let),
                        enclosingSource);
                    DiscoverSourceFunctionOrigins(let.Value, declared ?? enclosingSource);
                    DiscoverSourceFunctionOrigins(let.Body, enclosingSource);
                    return;
                }
            case Expr.Lambda lambda:
                DiscoverSourceFunctionOrigins(lambda.Body, enclosingSource);
                return;
            default:
                foreach (Expr child in EnumerateChildren(expression))
                {
                    DiscoverSourceFunctionOrigins(child, enclosingSource);
                }

                return;
        }
    }

    private SourceFunctionOrigin? DiscoverSourceFunctionOrigin(
        string name,
        Expr value,
        TextSpan nameSpan,
        SourceFunctionOrigin? enclosingSource)
    {
        if (name.StartsWith("__trait_validate_", StringComparison.Ordinal))
        {
            return null;
        }
        if (FindInnermostLambdaUnderLets(value) is not { } lambda)
        {
            return null;
        }

        SourceFunctionOrigin source = CreateSourceFunctionOrigin(name, nameSpan, enclosingSource);
        _sourceFunctionOriginsByLambda[lambda] = source;
        return source;
    }

    private IrFunctionOrigin CreateLambdaOrigin(
        Expr.Lambda lambda,
        string label,
        IrFunctionOriginSeed? seed)
    {
        if (seed is not null)
        {
            return new IrFunctionOrigin(
                label,
                seed.Kind,
                seed.Source,
                seed.ParentGeneratedLabel,
                seed.CompilerOwner,
                seed.StableDiscriminator,
                seed.GenerationLocation ?? ResolveSourceLocation(AstSpans.GetOrDefault(lambda)));
        }

        if (_sourceFunctionOriginsByLambda.TryGetValue(lambda, out SourceFunctionOrigin? source))
        {
            if (source.SourceName.StartsWith("__trait_validate_", StringComparison.Ordinal))
            {
                return new IrFunctionOrigin(
                    label,
                    IrFunctionOriginKind.ClosureHelper,
                    CompilerOwner: new CompilerFunctionOwner(
                        CompilerFunctionOwnerKind.Program,
                        "trait declaration validation"),
                    StableDiscriminator: source.SourceName,
                    GenerationLocation: ResolveSourceLocation(AstSpans.GetOrDefault(lambda)));
            }
            return new IrFunctionOrigin(
                label,
                IrFunctionOriginKind.SourceFunction,
                source,
                GenerationLocation: ResolveSourceLocation(AstSpans.GetOrDefault(lambda)));
        }

        TextSpan span = AstSpans.GetOrDefault(lambda);
        SourceLocation? location = ResolveSourceLocation(span);
        if (_activeFunctionOrigin is { } parent)
        {
            return new IrFunctionOrigin(
                label,
                IrFunctionOriginKind.ClosureHelper,
                parent.Source,
                parent.GeneratedLabel,
                StableDiscriminator: LambdaSiteDiscriminator(lambda, span),
                GenerationLocation: location);
        }

        return new IrFunctionOrigin(
            label,
            IrFunctionOriginKind.ClosureHelper,
            CompilerOwner: new CompilerFunctionOwner(
                CompilerFunctionOwnerKind.Program,
                "anonymous source function"),
            StableDiscriminator: LambdaSiteDiscriminator(lambda, span),
            GenerationLocation: location);
    }

    private static string LambdaSiteDiscriminator(Expr.Lambda lambda, TextSpan span)
        => $"lambda:{span.Start}:{span.Length}:{lambda.ParamName}";

    private void AddFunction(IrFunction function, IrFunctionOrigin origin)
    {
        if (!string.Equals(function.Label, origin.GeneratedLabel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Function origin label '{origin.GeneratedLabel}' does not match '{function.Label}'.");
        }

        _irFunctionOriginsByLabel[function.Label] = origin;
        if (origin.Kind == IrFunctionOriginKind.SourceFunction && origin.Source is { } source)
        {
            _primaryFunctionLabelBySource.TryAdd(source, function.Label);
        }

        if (_collectInferredTraitElaboration)
        {
            function = function with
            {
                Instructions = [],
                LocalNames = new Dictionary<int, string>(),
                LocalTypes = new Dictionary<int, TypeRef>(),
            };
        }
        else
        {
            CaptureValuePlacements(origin);
        }
        _funcs.Add(function with { Origin = origin });
    }

    private SourceFunctionOrigin? ResolveSourceFunctionOrigin(Expr.Lambda lambda, string name)
    {
        if (_sourceFunctionOriginsByLambda.TryGetValue(lambda, out SourceFunctionOrigin? exact))
        {
            return exact;
        }

        return _maNameIndex.TryGetValue(name, out FuncKey key)
            && _maFunctionOrigins.TryGetValue(key, out SourceFunctionOrigin? source)
                ? source
                : null;
    }

    private string? ResolvePrimaryFunctionLabel(SourceFunctionOrigin? source)
        => source is not null && _primaryFunctionLabelBySource.TryGetValue(source, out string? label)
            ? label
            : null;

    private IrFunctionOriginSeed CreateSpecializationOriginSeed(
        IrFunctionOriginKind kind,
        Expr.Lambda lambda,
        string name,
        string cacheKey)
    {
        SourceFunctionOrigin? source = ResolveSourceFunctionOrigin(lambda, name);
        return CreateGeneratedSourceSeed(
            kind,
            source,
            ResolvePrimaryFunctionLabel(source),
            cacheKey,
            lambda);
    }

    private void LowerSpecializationLambda(
        IrFunctionOriginKind kind,
        Expr.Lambda lambda,
        string name,
        TypeRef functionType,
        string label,
        string cacheKey)
    {
        LowerLambdaCore(
            lambda,
            selfName: name,
            selfType: functionType,
            stackAllocateClosure: false,
            forcedLabel: label,
            originSeed: CreateSpecializationOriginSeed(kind, lambda, name, cacheKey));
    }

    private void LowerReuseSpecializationLambda(
        Expr.Lambda lambda,
        string name,
        TypeRef functionType,
        string label,
        string cacheKey)
        => LowerSpecializationLambda(
            IrFunctionOriginKind.ReuseSpecialization,
            lambda,
            name,
            functionType,
            label,
            cacheKey);

    private void LowerParallelSpecializationLambda(
        Expr.Lambda lambda,
        string name,
        TypeRef functionType,
        string label,
        string cacheKey)
        => LowerSpecializationLambda(
            IrFunctionOriginKind.ParallelSpecialization,
            lambda,
            name,
            functionType,
            label,
            cacheKey);

    private void LowerTraitOperatorSpecializationLambda(
        Expr.Lambda lambda,
        string name,
        TypeRef functionType,
        string label,
        string cacheKey)
        => LowerSpecializationLambda(
            IrFunctionOriginKind.TraitOperatorSpecialization,
            lambda,
            name,
            functionType,
            label,
            cacheKey);

    // slotCount is the dispatch function's parameter-slot count (the shared arity for a group of
    // identical signatures); the discriminator keeps its historical "arity:" spelling so the
    // stable origin identity of every previously merged group is unchanged.
    private IrFunctionOriginSeed CreateMutualRecursionDispatchOriginSeed(
        RecursiveGroupExpr group,
        IReadOnlyList<(string Name, Expr Value)> bindings,
        int slotCount)
    {
        string ownerName = string.Join(
            ",",
            Enumerable.Range(0, bindings.Count)
                .Select(index =>
                {
                    SourceFunctionOrigin source =
                        _maFunctionOrigins[GetRecursiveGroupMemberKey(group, index)];
                    return $"{source.QualifiedName ?? source.SourceName}@{source.DeclarationOffset}";
                }));
        return CreateGeneratedOwnerSeed(
            IrFunctionOriginKind.MutualRecursionDispatch,
            CompilerFunctionOwnerKind.MutualRecursionGroup,
            ownerName,
            stableDiscriminator: $"arity:{slotCount}");
    }

    private IrFunctionOrigin CreateClosureNormalizerOrigin(
        string label,
        string closureLabel,
        IReadOnlyList<(int EnvOffset, TypeRef Type)> captures)
    {
        _irFunctionOriginsByLabel.TryGetValue(closureLabel, out IrFunctionOrigin? parentOrigin);
        string captureLayout = string.Join(
            ";",
            captures.Select(capture => $"{capture.EnvOffset}:{Pretty(capture.Type)}"));
        return new IrFunctionOrigin(
            label,
            IrFunctionOriginKind.ClosureEnvironmentNormalizer,
            parentOrigin?.Source,
            closureLabel,
            parentOrigin is null
                ? new CompilerFunctionOwner(
                    CompilerFunctionOwnerKind.RuntimeLayout,
                    captureLayout)
                : null,
            captureLayout);
    }

    private IrFunctionOriginSeed CreateGeneratedSourceSeed(
        IrFunctionOriginKind kind,
        SourceFunctionOrigin? source,
        string? parentGeneratedLabel,
        string? stableDiscriminator,
        Expr? generationExpression = null)
        => new(
            kind,
            source,
            parentGeneratedLabel,
            StableDiscriminator: stableDiscriminator,
            GenerationLocation: generationExpression is null
                ? null
                : ResolveSourceLocation(AstSpans.GetOrDefault(generationExpression)));

    private IrFunctionOriginSeed CreateGeneratedOwnerSeed(
        IrFunctionOriginKind kind,
        CompilerFunctionOwnerKind ownerKind,
        string ownerName,
        string? stableDiscriminator = null,
        SourceLocation? generationLocation = null)
        => new(
            kind,
            CompilerOwner: new CompilerFunctionOwner(ownerKind, ownerName),
            StableDiscriminator: stableDiscriminator,
            GenerationLocation: generationLocation);
}
