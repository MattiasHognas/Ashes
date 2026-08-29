using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private enum PatternBindingUseContext
    {
        StructuralInspection,
        EmbeddedInOwner,
        IndependentEscape,
        CapturedByClosure,
        ConservativeUnknown,
    }

    private sealed class PatternBindingOwnershipBuilder(
        Pattern.Var binder,
        SourceFunctionOrigin function,
        int bindingOrdinal,
        int rootParameterOrdinal,
        string rootParameterName,
        int? parentBindingOrdinal,
        int extractionDepth,
        SourceLocation? location)
    {
        public Pattern.Var Binder { get; } = binder;
        public SourceFunctionOrigin Function { get; } = function;
        public int BindingOrdinal { get; } = bindingOrdinal;
        public int RootParameterOrdinal { get; } = rootParameterOrdinal;
        public string RootParameterName { get; } = rootParameterName;
        public int? ParentBindingOrdinal { get; } = parentBindingOrdinal;
        public int ExtractionDepth { get; } = extractionDepth;
        public SourceLocation? Location { get; } = location;
        public PatternBindingOwnershipUse Uses { get; set; }

        public PatternBindingOwnershipFact Build()
        {
            return new PatternBindingOwnershipFact(
                Binder,
                Function,
                BindingOrdinal,
                Binder.Name,
                RootParameterOrdinal,
                RootParameterName,
                ParentBindingOrdinal,
                ExtractionDepth,
                Uses,
                ClassifyPatternBindingOwnership(Uses),
                Location);
        }
    }

    private readonly record struct PatternBindingLineage(
        int RootParameterOrdinal,
        string RootParameterName,
        int ExtractionDepth,
        PatternBindingOwnershipBuilder? Binding);

    private sealed class PatternBindingOwnershipState(
        FuncKey function,
        string selfName,
        IReadOnlyList<string> parameterNames,
        SourceFunctionOrigin origin)
    {
        public FuncKey Function { get; } = function;
        public string SelfName { get; } = selfName;
        public IReadOnlyList<string> ParameterNames { get; } = parameterNames;
        public SourceFunctionOrigin Origin { get; } = origin;
        public List<PatternBindingOwnershipBuilder> Bindings { get; } = [];
    }

    private IReadOnlyList<PatternBindingOwnershipFact> ComputePatternBindingOwnership(
        FuncKey function,
        (List<string> Params, Expr Body) info)
    {
        IReadOnlyDictionary<string, FuncKey> functionScope =
            _maFunctionScopes.GetValueOrDefault(function)
            ?? new Dictionary<string, FuncKey>(StringComparer.Ordinal);
        PatternBindingOwnershipState state = new(
            function,
            _maKeyName[function],
            info.Params,
            _maFunctionOrigins[function]);
        Dictionary<string, PatternBindingLineage> lineages = new(StringComparer.Ordinal);
        for (int i = 0; i < info.Params.Count; i++)
        {
            lineages[info.Params[i]] = new PatternBindingLineage(i, info.Params[i], 0, null);
        }

        WalkPatternBindingOwnership(
            info.Body,
            lineages,
            functionScope,
            state,
            PatternBindingUseContext.IndependentEscape);
        return state.Bindings.Select(binding => binding.Build()).ToList();
    }

    private void WalkPatternBindingOwnership(
        Expr expression,
        IReadOnlyDictionary<string, PatternBindingLineage> lineages,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        PatternBindingOwnershipState state,
        PatternBindingUseContext context)
    {
        switch (expression)
        {
            case Expr.Var variable:
                RecordPatternBindingUse(variable, lineages, context);
                return;
            case Expr.QualifiedVar:
            case Expr.IntLit:
            case Expr.BigIntLit:
            case Expr.UIntLit:
            case Expr.FloatLit:
            case Expr.StrLit or Expr.RuneLit:
            case Expr.BoolLit:
                return;
            case Expr.If conditional:
                WalkPatternBindingOwnership(conditional.Cond, lineages, functionScope, state, PatternBindingUseContext.StructuralInspection);
                WalkPatternBindingOwnership(conditional.Then, lineages, functionScope, state, context);
                WalkPatternBindingOwnership(conditional.Else, lineages, functionScope, state, context);
                return;
            // `&&`/`||` are short-circuit — like `If`, not like an eager binary operator below: the
            // left operand is always evaluated (a real condition, `StructuralInspection`) but the
            // right operand only runs conditionally, in the position an `If`'s taken branch would
            // (so it propagates the caller's own `context` rather than getting a fixed one).
            case Expr.LogicalAnd and:
                WalkPatternBindingOwnership(and.Left, lineages, functionScope, state, PatternBindingUseContext.StructuralInspection);
                WalkPatternBindingOwnership(and.Right, lineages, functionScope, state, context);
                return;
            case Expr.LogicalOr or:
                WalkPatternBindingOwnership(or.Left, lineages, functionScope, state, PatternBindingUseContext.StructuralInspection);
                WalkPatternBindingOwnership(or.Right, lineages, functionScope, state, context);
                return;
            case Expr.Let let:
                WalkPatternBindingOwnershipLet(let, lineages, functionScope, state, context);
                return;
            case Expr.LetResult letResult:
                WalkPatternBindingOwnershipLetResult(letResult, lineages, functionScope, state, context);
                return;
            case Expr.LetRecursive letRecursive:
                WalkPatternBindingOwnershipLetRecursive(letRecursive, lineages, functionScope, state, context);
                return;
            case Expr.Lambda lambda:
                RecordPatternBindingFreeUses(
                    lambda.Body,
                    RemovePatternBindingLineages(lineages, [lambda.ParamName]),
                    PatternBindingUseContext.CapturedByClosure);
                return;
            case Expr.Match match:
                WalkPatternBindingOwnershipMatch(match, lineages, functionScope, state, context);
                return;
            case Expr.Call:
                WalkPatternBindingOwnershipCall(expression, lineages, functionScope, state);
                return;
            default:
                WalkPatternBindingOwnershipComposite(expression, lineages, functionScope, state);
                return;
        }
    }

    private void WalkPatternBindingOwnershipComposite(
        Expr expression,
        IReadOnlyDictionary<string, PatternBindingLineage> lineages,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        PatternBindingOwnershipState state)
    {
        switch (expression)
        {
            case Expr.TupleLit tuple:
                WalkPatternBindingOwnershipAll(tuple.Elements, lineages, functionScope, state, PatternBindingUseContext.EmbeddedInOwner);
                return;
            case Expr.ListLit list:
                WalkPatternBindingOwnershipAll(list.Elements, lineages, functionScope, state, PatternBindingUseContext.EmbeddedInOwner);
                return;
            case Expr.Cons cons:
                WalkPatternBindingOwnershipAll([cons.Head, cons.Tail], lineages, functionScope, state, PatternBindingUseContext.EmbeddedInOwner);
                return;
            case Expr.RecordLit record:
                WalkPatternBindingOwnershipAll(record.Fields.Select(field => field.Value), lineages, functionScope, state, PatternBindingUseContext.EmbeddedInOwner);
                return;
            case Expr.RecordUpdate update:
                WalkPatternBindingOwnershipAll(
                    [update.Target, .. update.Updates.Select(field => field.Value)],
                    lineages,
                    functionScope,
                    state,
                    PatternBindingUseContext.EmbeddedInOwner);
                return;
            case Expr.Add or Expr.Subtract or Expr.Multiply or Expr.Divide or Expr.Modulo
                or Expr.BitwiseAnd or Expr.BitwiseOr or Expr.BitwiseXor or Expr.ShiftLeft
                or Expr.ShiftRight or Expr.BitwiseNot or Expr.LogicalNot or Expr.GreaterThan or Expr.LessThan
                or Expr.GreaterOrEqual or Expr.LessOrEqual or Expr.Equal or Expr.NotEqual:
                WalkPatternBindingOwnershipAll(
                    EnumerateChildren(expression),
                    lineages,
                    functionScope,
                    state,
                    PatternBindingUseContext.StructuralInspection);
                return;
            case Expr.ResultPipe or Expr.ResultMapErrorPipe or Expr.Await:
                WalkPatternBindingOwnershipAll(
                    EnumerateChildren(expression),
                    lineages,
                    functionScope,
                    state,
                    PatternBindingUseContext.ConservativeUnknown);
                return;
            case Expr.Handle handle:
                RecordPatternBindingFreeUses(handle, lineages, PatternBindingUseContext.ConservativeUnknown);
                return;
            case RecursiveGroupExpr group:
                RecordPatternBindingFreeUses(group, lineages, PatternBindingUseContext.ConservativeUnknown);
                return;
            case CapabilityPostExpr post:
                RecordPatternBindingFreeUses(post, lineages, PatternBindingUseContext.ConservativeUnknown);
                return;
            default:
                RecordPatternBindingFreeUses(expression, lineages, PatternBindingUseContext.ConservativeUnknown);
                return;
        }
    }

    private void WalkPatternBindingOwnershipAll(
        IEnumerable<Expr> expressions,
        IReadOnlyDictionary<string, PatternBindingLineage> lineages,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        PatternBindingOwnershipState state,
        PatternBindingUseContext context)
    {
        foreach (Expr expression in expressions)
        {
            WalkPatternBindingOwnership(expression, lineages, functionScope, state, context);
        }
    }

    private void WalkPatternBindingOwnershipLet(
        Expr.Let let,
        IReadOnlyDictionary<string, PatternBindingLineage> lineages,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        PatternBindingOwnershipState state,
        PatternBindingUseContext context)
    {
        IReadOnlyDictionary<string, FuncKey> bodyScope =
            ExtendTcoFuncScope(functionScope, let, let.Name, let.Value);
        if (let.Value is Expr.Var alias && lineages.TryGetValue(alias.Name, out PatternBindingLineage lineage))
        {
            WalkPatternBindingOwnership(
                let.Body,
                SetPatternBindingLineage(lineages, let.Name, lineage),
                bodyScope,
                state,
                context);
            return;
        }

        WalkPatternBindingOwnership(let.Value, lineages, functionScope, state, PatternBindingUseContext.IndependentEscape);
        WalkPatternBindingOwnership(
            let.Body,
            RemovePatternBindingLineages(lineages, [let.Name]),
            bodyScope,
            state,
            context);
    }

    private void WalkPatternBindingOwnershipLetResult(
        Expr.LetResult letResult,
        IReadOnlyDictionary<string, PatternBindingLineage> lineages,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        PatternBindingOwnershipState state,
        PatternBindingUseContext context)
    {
        WalkPatternBindingOwnership(letResult.Value, lineages, functionScope, state, PatternBindingUseContext.ConservativeUnknown);
        WalkPatternBindingOwnership(
            letResult.Body,
            RemovePatternBindingLineages(lineages, [letResult.Name]),
            ExtendFuncScope(functionScope, letResult, letResult.Name),
            state,
            context);
    }

    private void WalkPatternBindingOwnershipLetRecursive(
        Expr.LetRecursive letRecursive,
        IReadOnlyDictionary<string, PatternBindingLineage> lineages,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        PatternBindingOwnershipState state,
        PatternBindingUseContext context)
    {
        IReadOnlyDictionary<string, PatternBindingLineage> bodyLineages =
            RemovePatternBindingLineages(lineages, [letRecursive.Name]);
        IReadOnlyDictionary<string, FuncKey> bodyScope =
            ExtendFuncScope(functionScope, letRecursive, letRecursive.Name);
        WalkPatternBindingOwnership(letRecursive.Value, bodyLineages, bodyScope, state, PatternBindingUseContext.ConservativeUnknown);
        WalkPatternBindingOwnership(letRecursive.Body, bodyLineages, bodyScope, state, context);
    }

    private void WalkPatternBindingOwnershipMatch(
        Expr.Match match,
        IReadOnlyDictionary<string, PatternBindingLineage> lineages,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        PatternBindingOwnershipState state,
        PatternBindingUseContext context)
    {
        PatternBindingLineage? sourceLineage = match.Value is Expr.Var source
            && lineages.TryGetValue(source.Name, out PatternBindingLineage found)
                ? found
                : null;
        if (sourceLineage is { } sourceValue)
        {
            sourceValue.Binding?.Uses |= PatternBindingOwnershipUse.StructuralInspection;
        }
        else
        {
            WalkPatternBindingOwnership(match.Value, lineages, functionScope, state, PatternBindingUseContext.StructuralInspection);
        }

        foreach (MatchCase matchCase in match.Cases)
        {
            IReadOnlyDictionary<string, PatternBindingLineage> armLineages =
                BindPatternOwnershipLineages(matchCase.Pattern, sourceLineage, lineages, state);
            HashSet<string> binders = CollectPatternOwnershipBinderNames(matchCase.Pattern);
            IReadOnlyDictionary<string, FuncKey> armFunctionScope = RemoveFuncNames(functionScope, binders);
            if (matchCase.Guard is not null)
            {
                WalkPatternBindingOwnership(
                    matchCase.Guard,
                    armLineages,
                    armFunctionScope,
                    state,
                    PatternBindingUseContext.StructuralInspection);
            }

            WalkPatternBindingOwnership(matchCase.Body, armLineages, armFunctionScope, state, context);
        }
    }

    private IReadOnlyDictionary<string, PatternBindingLineage> BindPatternOwnershipLineages(
        Pattern pattern,
        PatternBindingLineage? source,
        IReadOnlyDictionary<string, PatternBindingLineage> lineages,
        PatternBindingOwnershipState state)
    {
        HashSet<string> names = CollectPatternOwnershipBinderNames(pattern);
        Dictionary<string, PatternBindingLineage> result = new(
            RemovePatternBindingLineages(lineages, names),
            StringComparer.Ordinal);
        if (source is not { } sourceLineage)
        {
            return result;
        }

        foreach ((Pattern.Var binder, int relativeDepth) in EnumeratePatternOwnershipBinders(pattern, 1))
        {
            if (_constructorSymbols.TryGetValue(binder.Name, out ConstructorSymbol? constructor)
                && constructor.Arity == 0)
            {
                continue;
            }

            PatternBindingOwnershipBuilder builder = new(
                binder,
                state.Origin,
                state.Bindings.Count,
                sourceLineage.RootParameterOrdinal,
                sourceLineage.RootParameterName,
                sourceLineage.Binding?.BindingOrdinal,
                sourceLineage.ExtractionDepth + relativeDepth,
                ResolveSourceLocation(AstSpans.GetOrDefault(binder)));
            state.Bindings.Add(builder);
            result[binder.Name] = new PatternBindingLineage(
                sourceLineage.RootParameterOrdinal,
                sourceLineage.RootParameterName,
                builder.ExtractionDepth,
                builder);
        }

        return result;
    }

    private void WalkPatternBindingOwnershipCall(
        Expr expression,
        IReadOnlyDictionary<string, PatternBindingLineage> lineages,
        IReadOnlyDictionary<string, FuncKey> functionScope,
        PatternBindingOwnershipState state)
    {
        List<Expr> arguments = [];
        Expr root = CollectCallArgs(expression, arguments);
        bool exactSelfCall = root is Expr.Var callee
            && string.Equals(callee.Name, state.SelfName, StringComparison.Ordinal)
            && functionScope.TryGetValue(callee.Name, out FuncKey target)
            && target.Equals(state.Function)
            && arguments.Count == state.ParameterNames.Count;
        string? constructorName = root switch
        {
            Expr.Var constructor => constructor.Name,
            Expr.QualifiedVar constructor => constructor.Name,
            _ => null,
        };
        bool constructorCall = constructorName is not null
            && _constructorSymbols.ContainsKey(constructorName);
        bool ordinaryCall = (root is Expr.Var or Expr.QualifiedVar) && !constructorCall;

        for (int i = 0; i < arguments.Count; i++)
        {
            Expr argument = arguments[i];
            if (argument is Expr.Var variable
                && lineages.TryGetValue(variable.Name, out PatternBindingLineage lineage)
                && lineage.Binding is { } binding)
            {
                binding.Uses |= exactSelfCall && lineage.RootParameterOrdinal == i
                    ? PatternBindingOwnershipUse.SameParameterTransfer
                    : exactSelfCall
                        ? PatternBindingOwnershipUse.IndependentEscape
                        : constructorCall
                            ? PatternBindingOwnershipUse.EmbeddedInOwner
                            : ordinaryCall
                                ? ClassifyOrdinaryCallArgumentUse(binding)
                                : PatternBindingOwnershipUse.ConservativeUnknown;
                continue;
            }

            WalkPatternBindingOwnership(
                argument,
                lineages,
                functionScope,
                state,
                exactSelfCall
                    ? PatternBindingUseContext.IndependentEscape
                    : constructorCall
                        ? PatternBindingUseContext.EmbeddedInOwner
                        : ordinaryCall
                            ? PatternBindingUseContext.StructuralInspection
                            : PatternBindingUseContext.ConservativeUnknown);
        }

        WalkPatternBindingOwnership(
            root,
            lineages,
            functionScope,
            state,
            PatternBindingUseContext.ConservativeUnknown);
    }

    /// <summary>
    /// A binding extracted directly off the scrutinee (depth 1, e.g. the head of a matched list) that
    /// is only ever passed to a plain call stays a borrow: whatever that call does with it, the
    /// call's own result is what a later `let` (or the same depth-1 slot on a tail self-call) would
    /// independently protect before it can escape further — that path already has its own correct
    /// ownership tracking. Verified against a real regression (fannkuch-redux, factorial-scaling
    /// leak): protecting a depth-1 binding here spuriously bumps its own refcount, which corrupts an
    /// unrelated uniqueness check further down the same call's own structural drop.
    ///
    /// A binding extracted one level deeper (depth 2+, e.g. a field pulled out of a record/constructor
    /// that is itself a list element) has no such safety net: the field's own reference is not the
    /// scrutinee's single tracked unit the way a depth-1 element is, so nothing else notices when the
    /// scrutinee's later deep-drop independently releases that same field. Treat it as an escape so it
    /// gets its own protective reference at the call site (DuplicatePerceusPatternOwnerForAggregate).
    /// </summary>
    private static PatternBindingOwnershipUse ClassifyOrdinaryCallArgumentUse(
        PatternBindingOwnershipBuilder binding) =>
        binding.ExtractionDepth >= 2
            ? PatternBindingOwnershipUse.IndependentEscape
            : PatternBindingOwnershipUse.OrdinaryCallBorrow;

    private static void RecordPatternBindingUse(
        Expr.Var variable,
        IReadOnlyDictionary<string, PatternBindingLineage> lineages,
        PatternBindingUseContext context)
    {
        if (lineages.TryGetValue(variable.Name, out PatternBindingLineage lineage)
            && lineage.Binding is { } binding)
        {
            binding.Uses |= context switch
            {
                PatternBindingUseContext.StructuralInspection => PatternBindingOwnershipUse.StructuralInspection,
                PatternBindingUseContext.EmbeddedInOwner => PatternBindingOwnershipUse.EmbeddedInOwner,
                PatternBindingUseContext.IndependentEscape => PatternBindingOwnershipUse.IndependentEscape,
                PatternBindingUseContext.CapturedByClosure => PatternBindingOwnershipUse.CapturedByClosure,
                _ => PatternBindingOwnershipUse.ConservativeUnknown,
            };
        }
    }

    private void RecordPatternBindingFreeUses(
        Expr expression,
        IReadOnlyDictionary<string, PatternBindingLineage> lineages,
        PatternBindingUseContext context)
    {
        HashSet<string> free = FreeVars(expression, new HashSet<string>(StringComparer.Ordinal));
        foreach (string name in free)
        {
            if (lineages.TryGetValue(name, out PatternBindingLineage lineage)
                && lineage.Binding is { } binding)
            {
                binding.Uses |= context switch
                {
                    PatternBindingUseContext.StructuralInspection => PatternBindingOwnershipUse.StructuralInspection,
                    PatternBindingUseContext.EmbeddedInOwner => PatternBindingOwnershipUse.EmbeddedInOwner,
                    PatternBindingUseContext.IndependentEscape => PatternBindingOwnershipUse.IndependentEscape,
                    PatternBindingUseContext.CapturedByClosure => PatternBindingOwnershipUse.CapturedByClosure,
                    _ => PatternBindingOwnershipUse.ConservativeUnknown,
                };
            }
        }
    }

    private static IReadOnlyDictionary<string, PatternBindingLineage> RemovePatternBindingLineages(
        IReadOnlyDictionary<string, PatternBindingLineage> lineages,
        IEnumerable<string> names)
    {
        var result = new Dictionary<string, PatternBindingLineage>(lineages, StringComparer.Ordinal);
        foreach (string name in names)
        {
            result.Remove(name);
        }

        return result;
    }

    private static IReadOnlyDictionary<string, PatternBindingLineage> SetPatternBindingLineage(
        IReadOnlyDictionary<string, PatternBindingLineage> lineages,
        string name,
        PatternBindingLineage lineage)
    {
        var result = new Dictionary<string, PatternBindingLineage>(lineages, StringComparer.Ordinal)
        {
            [name] = lineage,
        };
        return result;
    }

    private static IEnumerable<(Pattern.Var Binder, int RelativeDepth)> EnumeratePatternOwnershipBinders(
        Pattern pattern,
        int depth)
    {
        switch (pattern)
        {
            case Pattern.Var binder:
                yield return (binder, depth);
                yield break;
            case Pattern.Cons cons:
                foreach ((Pattern.Var binder, int relativeDepth) in EnumeratePatternOwnershipChild(cons.Head, depth))
                {
                    yield return (binder, relativeDepth);
                }

                foreach ((Pattern.Var binder, int relativeDepth) in EnumeratePatternOwnershipChild(cons.Tail, depth))
                {
                    yield return (binder, relativeDepth);
                }

                yield break;
            case Pattern.Tuple tuple:
                foreach (Pattern element in tuple.Elements)
                {
                    foreach ((Pattern.Var binder, int relativeDepth) in EnumeratePatternOwnershipChild(element, depth))
                    {
                        yield return (binder, relativeDepth);
                    }
                }
                yield break;
            case Pattern.Constructor constructor:
                foreach (Pattern child in constructor.Patterns)
                {
                    foreach ((Pattern.Var binder, int relativeDepth) in EnumeratePatternOwnershipChild(child, depth))
                    {
                        yield return (binder, relativeDepth);
                    }
                }
                yield break;
            case Pattern.Record record:
                foreach ((string _, Pattern child) in record.Fields)
                {
                    foreach ((Pattern.Var binder, int relativeDepth) in EnumeratePatternOwnershipChild(child, depth))
                    {
                        yield return (binder, relativeDepth);
                    }
                }

                yield break;
            case Pattern.As asPattern:
                foreach ((Pattern.Var binder, int relativeDepth) in EnumeratePatternOwnershipBinders(asPattern.Inner, depth))
                {
                    yield return (binder, relativeDepth);
                }
                yield break;
            case Pattern.Or { Alternatives.Count: > 0 } orPattern:
                foreach ((Pattern.Var binder, int relativeDepth) in EnumeratePatternOwnershipBinders(orPattern.Alternatives[0], depth))
                {
                    yield return (binder, relativeDepth);
                }

                yield break;
        }
    }

    private static IEnumerable<(Pattern.Var Binder, int RelativeDepth)> EnumeratePatternOwnershipChild(
        Pattern pattern,
        int parentDepth)
    {
        int childDepth = pattern is Pattern.Var ? parentDepth : parentDepth + 1;
        return EnumeratePatternOwnershipBinders(pattern, childDepth);
    }

    private HashSet<string> CollectPatternOwnershipBinderNames(Pattern pattern)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach ((Pattern.Var binder, int _) in EnumeratePatternOwnershipBinders(pattern, 1))
        {
            if (!_constructorSymbols.TryGetValue(binder.Name, out ConstructorSymbol? constructor)
                || constructor.Arity != 0)
            {
                result.Add(binder.Name);
            }
        }

        return result;
    }

    private static PatternBindingOwnershipKind ClassifyPatternBindingOwnership(
        PatternBindingOwnershipUse uses)
    {
        if ((uses & PatternBindingOwnershipUse.ConservativeUnknown) != PatternBindingOwnershipUse.None)
        {
            return PatternBindingOwnershipKind.ConservativeUnknown;
        }

        if ((uses & (PatternBindingOwnershipUse.IndependentEscape
            | PatternBindingOwnershipUse.CapturedByClosure)) != PatternBindingOwnershipUse.None)
        {
            return PatternBindingOwnershipKind.EscapesIndependently;
        }

        if ((uses & PatternBindingOwnershipUse.EmbeddedInOwner) != PatternBindingOwnershipUse.None)
        {
            return PatternBindingOwnershipKind.EmbeddedInOwner;
        }

        return (uses & PatternBindingOwnershipUse.SameParameterTransfer) != PatternBindingOwnershipUse.None
            ? PatternBindingOwnershipKind.TransferredToSameParameter
            : PatternBindingOwnershipKind.BorrowedOnly;
    }
}
