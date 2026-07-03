using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // Effect diagnostic codes. Defined locally because the shared DiagnosticCodes table
    // lives in Ashes.Frontend.
    private const string UnhandledEffectCode = "ASH017";
    private const string EffectNotPermittedCode = "ASH018";
    private const string UnknownEffectCode = "ASH019";
    private const string InvalidHandlerCode = "ASH020";

    // Declared effects by name.
    private readonly Dictionary<string, EffectSymbol> _effectSymbols = new(StringComparer.Ordinal);

    // First perform-site span per effect, giving the unhandled-effect diagnostic (ASH017) a
    // useful location to point at.
    private readonly Dictionary<string, TextSpan> _firstPerformSites = new(StringComparer.Ordinal);

    // The ambient effect row of the code currently being lowered: every operation performed and
    // every effectful call in a scope inserts its effects here. Each lambda body gets its own row
    // (the lambda's arrow row); the field holds the entry expression's row otherwise. Created
    // lazily because type-variable numbering starts with the program.
    private TypeRef? _ambientRow;

    private TypeRef AmbientRow => _ambientRow ??= NewTypeVar();

    private void RegisterEffectDeclarations(IReadOnlyList<TopLevelItem> items)
    {
        foreach (var item in items.OfType<TopLevelItem.Effect>())
        {
            var decl = item.Decl;
            if (_effectSymbols.ContainsKey(decl.Name))
            {
                ReportDiagnostic(GetSpan(decl), $"Duplicate effect name '{decl.Name}'.");
                continue;
            }

            var typeParameters = decl.TypeParameters
                .Select(tp => new TypeParameterSymbol(tp.Name))
                .ToList();

            var operations = new Dictionary<string, EffectOperationSymbol>(StringComparer.Ordinal);
            foreach (var operation in decl.Operations)
            {
                if (operations.ContainsKey(operation.Name))
                {
                    ReportDiagnostic(GetSpan(decl), $"Duplicate operation '{operation.Name}' in effect '{decl.Name}'.");
                    continue;
                }

                TypeRef? declaredSignature = null;
                TypeRef? inferredType = null;
                if (operation.Signature is not null)
                {
                    declaredSignature = ResolveAnnotationType(operation.Signature, typeParameters);
                }
                else if (typeParameters.Count > 0)
                {
                    // Without a signature there is nothing tying the operation's type to the
                    // effect's parameters, so a polymorphic operation must be annotated.
                    ReportDiagnostic(
                        GetSpan(decl),
                        $"Operation '{operation.Name}' of parameterized effect '{decl.Name}' requires an explicit signature.",
                        UnknownEffectCode);
                    inferredType = NewTypeVar();
                }
                else
                {
                    // Unsigned operation: one shared inference variable, unified across every
                    // perform-site (and, in Stage 2, every handler arm) in the compilation unit.
                    inferredType = NewTypeVar();
                }

                operations[operation.Name] = new EffectOperationSymbol(operation.Name, declaredSignature, inferredType);
            }

            _effectSymbols[decl.Name] = new EffectSymbol(decl.Name, typeParameters, operations, decl);
        }
    }

    private static TextSpan GetSpan(EffectDecl effectDecl)
    {
        var span = AstSpans.GetOrDefault(effectDecl);
        return span.Length == 0 ? TextSpan.FromBounds(span.Start, span.Start + 1) : span;
    }

    // ---------------- Row normalization and unification ----------------

    /// <summary>
    /// Normalizes a row to its concrete effects (deduped by effect name, unifying the type
    /// arguments of duplicates) plus the open tail variable, or null when closed. A null row is
    /// the pure closed empty row.
    /// </summary>
    private (List<TypeRef.TEffect> Effects, TypeRef.TVar? Tail) NormalizeRow(TypeRef? row)
    {
        var effects = new List<TypeRef.TEffect>();
        TypeRef.TVar? tail = null;

        void Add(TypeRef.TEffect effect)
        {
            foreach (var existing in effects)
            {
                if (string.Equals(existing.Symbol.Name, effect.Symbol.Name, StringComparison.Ordinal))
                {
                    // A row contains at most one instance of an effect; a second mention unifies
                    // the instances' type arguments.
                    for (int i = 0; i < Math.Min(existing.Args.Count, effect.Args.Count); i++)
                    {
                        Unify(existing.Args[i], effect.Args[i]);
                    }

                    return;
                }
            }

            effects.Add(effect);
        }

        void Walk(TypeRef? r)
        {
            if (r is null)
            {
                return;
            }

            switch (Prune(r))
            {
                case TypeRef.TRow tr:
                    foreach (var effect in tr.Effects)
                    {
                        Add(effect);
                    }

                    Walk(tr.Tail);
                    return;
                case TypeRef.TVar v:
                    tail = v;
                    return;
                default:
                    // Ill-kinded tail (never produced by construction); unification reports the
                    // mismatch elsewhere.
                    return;
            }
        }

        Walk(row);
        return (effects, tail);
    }

    /// <summary>
    /// Row unification: effects present on both sides unify their type arguments; effects present
    /// on one side only must be absorbed by the other side's tail variable (a closed side that is
    /// missing an effect is an error). Open tails are re-linked through a shared fresh tail.
    /// </summary>
    private void UnifyRows(TypeRef? a, TypeRef? b)
    {
        var (effectsA, tailA) = NormalizeRow(a);
        var (effectsB, tailB) = NormalizeRow(b);

        var onlyA = new List<TypeRef.TEffect>();
        foreach (var effectA in effectsA)
        {
            TypeRef.TEffect? match = null;
            foreach (var effectB in effectsB)
            {
                if (string.Equals(effectA.Symbol.Name, effectB.Symbol.Name, StringComparison.Ordinal))
                {
                    match = effectB;
                    break;
                }
            }

            if (match is null)
            {
                onlyA.Add(effectA);
            }
            else
            {
                for (int i = 0; i < Math.Min(effectA.Args.Count, match.Args.Count); i++)
                {
                    Unify(effectA.Args[i], match.Args[i]);
                }
            }
        }

        var onlyB = effectsB
            .Where(effectB => !effectsA.Any(effectA => string.Equals(effectA.Symbol.Name, effectB.Symbol.Name, StringComparison.Ordinal)))
            .ToList();

        if (onlyA.Count == 0 && onlyB.Count == 0)
        {
            if (tailA is not null && tailB is not null)
            {
                if (tailA.Id != tailB.Id)
                {
                    Unify(tailA, tailB);
                }
            }
            else if (tailA is not null)
            {
                Unify(tailA, new TypeRef.TRow([], null));
            }
            else if (tailB is not null)
            {
                Unify(tailB, new TypeRef.TRow([], null));
            }

            return;
        }

        if (onlyB.Count > 0 && tailA is null)
        {
            ReportRowMissingEffects(onlyB, effectsA);
            return;
        }

        if (onlyA.Count > 0 && tailB is null)
        {
            ReportRowMissingEffects(onlyA, effectsB);
            return;
        }

        if (tailA is not null && tailB is not null && tailA.Id == tailB.Id)
        {
            // One tail cannot absorb different effect sets on both sides.
            ReportRowMissingEffects(onlyA.Concat(onlyB).ToList(), []);
            return;
        }

        // Absorb each side's surplus into the other side's tail, sharing one fresh tail when both
        // rows are open so the result stays a single open row.
        var sharedTail = tailA is not null && tailB is not null ? NewTypeVar() : null;
        if (tailA is not null && onlyB.Count > 0)
        {
            Unify(tailA, new TypeRef.TRow(onlyB, tailB is null ? null : sharedTail));
        }
        else if (tailA is not null && tailB is not null)
        {
            Unify(tailA, sharedTail!);
        }

        if (tailB is not null && onlyA.Count > 0)
        {
            Unify(tailB, new TypeRef.TRow(onlyA, tailA is null ? null : sharedTail));
        }
        else if (tailB is not null && tailA is not null && onlyB.Count == 0)
        {
            Unify(tailB, sharedTail!);
        }
    }

    private void ReportRowMissingEffects(List<TypeRef.TEffect> missing, List<TypeRef.TEffect> closedRowEffects)
    {
        var names = string.Join(", ", missing.Select(PrettyEffect).OrderBy(n => n, StringComparer.Ordinal));
        var row = string.Join(", ", closedRowEffects.Select(PrettyEffect).OrderBy(n => n, StringComparer.Ordinal));
        var plural = missing.Count == 1 ? $"Effect '{names}' is" : $"Effects '{names}' are";
        ReportDiagnostic(0, $"{plural} not permitted by the closed row uses {{{row}}}.", EffectNotPermittedCode);
    }

    private string PrettyEffect(TypeRef.TEffect effect)
    {
        return effect.Args.Count == 0
            ? effect.Symbol.Name
            : $"{effect.Symbol.Name}({string.Join(", ", effect.Args.Select(Pretty))})";
    }

    // ---------------- Ambient-row plumbing ----------------

    /// <summary>
    /// Records that calling a function with row <paramref name="calleeRow"/> performs that row's
    /// effects in the current context. An open (inferred) callee row is unified with the ambient
    /// row; a closed (annotated) row only requires its effects to be present — calling a
    /// <c>uses {Prices}</c> function from a <c>{Prices, Clock}</c> context is fine.
    /// </summary>
    private void SubsumeCalleeRow(TypeRef? calleeRow, TextSpan span)
    {
        if (calleeRow is null)
        {
            return;
        }

        var (effects, tail) = NormalizeRow(calleeRow);
        if (effects.Count == 0 && tail is null)
        {
            return;
        }

        foreach (var effect in effects)
        {
            RecordPerformSite(effect, span);
        }

        if (tail is not null)
        {
            UnifyRows(calleeRow, AmbientRow);
        }
        else
        {
            RequireEffectsInAmbient(effects);
        }
    }

    /// <summary>Requires the ambient row to include the given effects, extending its tail as needed.</summary>
    private void RequireEffectsInAmbient(List<TypeRef.TEffect> effects)
    {
        if (effects.Count == 0)
        {
            return;
        }

        UnifyRows(new TypeRef.TRow(effects, NewTypeVar()), AmbientRow);
    }

    private void RecordPerformSite(TypeRef.TEffect effect, TextSpan span)
    {
        if (span.Length > 0 && !_firstPerformSites.ContainsKey(effect.Symbol.Name))
        {
            _firstPerformSites[effect.Symbol.Name] = span;
        }
    }

    /// <summary>
    /// The end-of-program unhandled-effect check (ASH017): after lowering, any concrete effect
    /// left in the entry expression's row has no handler discharging it.
    /// </summary>
    private void CheckUnhandledEffects()
    {
        if (_ambientRow is null)
        {
            return;
        }

        var (effects, _) = NormalizeRow(_ambientRow);
        foreach (var effect in effects.OrderBy(e => e.Symbol.Name, StringComparer.Ordinal))
        {
            var span = _firstPerformSites.TryGetValue(effect.Symbol.Name, out var performSite)
                ? performSite
                : default;
            ReportDiagnostic(span, $"Unhandled effect '{effect.Symbol.Name}': no enclosing handler discharges it.", UnhandledEffectCode);
        }
    }

    // ---------------- Perform / operation calls / handle ----------------

    private (int, TypeRef) LowerPerform(Expr.Perform perform)
    {
        // `perform` is an optional no-op marker; it must wrap an effect operation call.
        var collectedArgs = new List<Expr>();
        var rootExpr = CollectCallArgs(perform.Operation, collectedArgs);
        if (rootExpr is Expr.QualifiedVar qv
            && _effectSymbols.TryGetValue(qv.Module, out var effectSym)
            && collectedArgs.Count > 0)
        {
            return LowerEffectOperationCall(effectSym, qv, collectedArgs);
        }

        ReportDiagnostic(GetSpan(perform), "'perform' must be applied to an effect operation call.", UnknownEffectCode);
        return LowerExpr(perform.Operation);
    }

    private (int, TypeRef) LowerEffectOperationCall(EffectSymbol effectSym, Expr.QualifiedVar qv, List<Expr> args)
    {
        var span = GetSpan(qv);
        if (!effectSym.Operations.TryGetValue(qv.Name, out var operation))
        {
            ReportDiagnostic(span, $"Effect '{effectSym.Name}' has no operation '{qv.Name}'.", UnknownEffectCode);
            foreach (var arg in args)
            {
                LowerExpr(arg);
            }

            return ReturnNeverWithDummyTemp();
        }

        // Instantiate the operation's type. A declared signature replaces the effect's type
        // parameters with fresh variables shared with the row entry, so `State(a)` ties
        // `get : Unit -> a` to the `State(a)` instance in the row. An unsigned operation uses its
        // shared inference variable (monomorphic within the compilation unit).
        var effectArgs = effectSym.TypeParameters.Select(_ => NewTypeVar()).ToList();
        var opType = operation.DeclaredSignature is not null
            ? InstantiateEffectSignature(operation.DeclaredSignature, effectSym.TypeParameters, effectArgs)
            : operation.InferredType!;

        var effectInstance = new TypeRef.TEffect(effectSym, effectArgs);
        RecordPerformSite(effectInstance, span);
        using (PushDiagnosticSpan(span))
        {
            RequireEffectsInAmbient([effectInstance]);
        }

        RecordHoverType(span, $"{effectSym.Name}.{qv.Name}", opType);

        // Type the application like an ordinary curried call chain.
        var currentType = opType;
        for (int i = 0; i < args.Count; i++)
        {
            var (_, argType) = LowerExpr(args[i]);
            currentType = Prune(currentType);
            if (currentType is TypeRef.TNever)
            {
                return ReturnNeverWithDummyTemp();
            }

            if (currentType is TypeRef.TVar)
            {
                Unify(currentType, new TypeRef.TFun(NewTypeVar(), NewTypeVar()));
                currentType = Prune(currentType);
            }

            if (currentType is not TypeRef.TFun fun)
            {
                ReportDiagnostic(span, $"Operation '{effectSym.Name}.{qv.Name}' expects {i} argument(s) but got {args.Count}.");
                return ReturnNeverWithDummyTemp();
            }

            using (PushDiagnosticContext($"in argument #{i + 1} of operation '{effectSym.Name}.{qv.Name}'"))
            {
                Unify(fun.Arg, argType);
            }

            // A signed operation may itself carry a row (an operation that performs other effects).
            SubsumeCalleeRow(fun.Row, span);
            currentType = Prune(fun.Ret);
        }

        // Stage 1 is typing-only: a program whose entry can reach a perform is rejected by the
        // unhandled-effect check, so this value is never observed at runtime.
        int temp = NewTemp();
        Emit(new IrInst.LoadConstInt(temp, 0));
        return (temp, currentType);
    }

    /// <summary>Substitutes an effect's type parameters with fresh per-use variables in an operation signature.</summary>
    private TypeRef InstantiateEffectSignature(TypeRef signature, IReadOnlyList<TypeParameterSymbol> typeParameters, IReadOnlyList<TypeRef> freshArgs)
    {
        if (typeParameters.Count == 0)
        {
            return signature;
        }

        TypeRef Walk(TypeRef t)
        {
            t = Prune(t);
            switch (t)
            {
                case TypeRef.TTypeParam tp:
                    for (int i = 0; i < typeParameters.Count; i++)
                    {
                        if (string.Equals(typeParameters[i].Name, tp.Symbol.Name, StringComparison.Ordinal))
                        {
                            return freshArgs[i];
                        }
                    }

                    return t;
                case TypeRef.TFun f:
                    return new TypeRef.TFun(Walk(f.Arg), Walk(f.Ret)) { Row = f.Row is null ? null : Walk(f.Row) };
                case TypeRef.TList l:
                    return new TypeRef.TList(Walk(l.Element));
                case TypeRef.TTuple tuple:
                    return new TypeRef.TTuple(tuple.Elements.Select(Walk).ToList());
                case TypeRef.TNamedType n:
                    return new TypeRef.TNamedType(n.Symbol, n.TypeArgs.Select(Walk).ToList());
                case TypeRef.TPtr p:
                    return new TypeRef.TPtr(Walk(p.Pointee));
                case TypeRef.TRow row:
                    return new TypeRef.TRow(
                        row.Effects.Select(e => new TypeRef.TEffect(e.Symbol, e.Args.Select(Walk).ToList())).ToList(),
                        row.Tail is null ? null : Walk(row.Tail));
                case TypeRef.TEffect effect:
                    return new TypeRef.TEffect(effect.Symbol, effect.Args.Select(Walk).ToList());
                default:
                    return t;
            }
        }

        return Walk(signature);
    }

    private (int, TypeRef) LowerHandle(Expr.Handle handle)
    {
        // Stage 2 (tail-resumptive handlers via evidence passing) is not implemented yet; reject
        // cleanly rather than miscompile. The arms are still walked so their own errors surface.
        ReportDiagnostic(GetSpan(handle), "'handle' expressions are not yet supported (effects Stage 2).", InvalidHandlerCode);
        return ReturnNeverWithDummyTemp();
    }

    /// <summary>Resolves a written uses row to a <see cref="TypeRef.TRow"/>.</summary>
    private TypeRef ResolveUsesRow(UsesRowSyntax row)
    {
        var effects = new List<TypeRef.TEffect>();
        foreach (var effectRef in row.Effects)
        {
            if (!_effectSymbols.TryGetValue(effectRef.Name, out var symbol))
            {
                ReportDiagnostic(0, $"Unknown effect '{effectRef.Name}' in uses row.", UnknownEffectCode);
                continue;
            }

            if (effectRef.Args.Count != symbol.TypeParameters.Count)
            {
                ReportDiagnostic(0, $"Effect '{effectRef.Name}' expects {symbol.TypeParameters.Count} type argument(s) but got {effectRef.Args.Count}.", UnknownEffectCode);
                continue;
            }

            effects.Add(new TypeRef.TEffect(symbol, effectRef.Args.Select(ResolveTypeExpr).ToList()));
        }

        TypeRef? tail = null;
        if (row.TailVar is not null)
        {
            // Row variables written in one annotation share one inference variable by name
            // (`uses {A | e}` and `uses e` in the same signature are the same row).
            _annotationRowVars ??= new Dictionary<string, TypeRef>(StringComparer.Ordinal);
            if (!_annotationRowVars.TryGetValue(row.TailVar, out tail))
            {
                tail = NewTypeVar();
                _annotationRowVars[row.TailVar] = tail;
            }
        }

        return new TypeRef.TRow(effects, tail);
    }

    // Named row variables of the annotation currently being resolved (shared by name within one
    // annotation, fresh across annotations), plus the type-parameter scope for effect operation
    // signatures (`effect State(a) = | get : Unit -> a`).
    private Dictionary<string, TypeRef>? _annotationRowVars;
    private Dictionary<string, TypeRef>? _typeExprParamScope;

    /// <summary>
    /// Resolves a user-written annotation with a fresh row-variable scope (and, for effect
    /// operation signatures, the effect's type parameters in scope).
    /// </summary>
    private TypeRef ResolveAnnotationType(TypeExpr typeExpr, IReadOnlyList<TypeParameterSymbol>? typeParamScope = null)
    {
        var savedRowVars = _annotationRowVars;
        var savedParamScope = _typeExprParamScope;
        _annotationRowVars = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
        _typeExprParamScope = typeParamScope?.ToDictionary(
            tp => tp.Name,
            tp => (TypeRef)new TypeRef.TTypeParam(tp),
            StringComparer.Ordinal);
        try
        {
            return ResolveTypeExpr(typeExpr);
        }
        finally
        {
            _annotationRowVars = savedRowVars;
            _typeExprParamScope = savedParamScope;
        }
    }
}
