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

    // Declared effects by name, plus their registration-order indices — the index selects the
    // effect's handler-evidence global and its snapshot slot inside every handler frame.
    private readonly Dictionary<string, EffectSymbol> _effectSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _effectIndices = new(StringComparer.Ordinal);

    // Unique suffix source for the labels of perform-site unhandled-guard blocks.
    private int _nextEffectSiteId;

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
            _effectIndices[decl.Name] = _effectIndices.Count;
        }
    }

    /// <summary>Number of declared effects: the backend materializes one handler-evidence global per effect.</summary>
    private int EffectGlobalCount => _effectSymbols.Count;

    /// <summary>The operation's slot index inside a handler frame for its effect (declaration order).</summary>
    private static int OperationDeclIndex(EffectSymbol effect, string opName)
    {
        for (int i = 0; i < effect.DeclaringSyntax.Operations.Count; i++)
        {
            if (string.Equals(effect.DeclaringSyntax.Operations[i].Name, opName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
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

        // Type the application like an ordinary curried call chain, collecting argument temps.
        var argTemps = new List<int>(args.Count);
        var currentType = opType;
        for (int i = 0; i < args.Count; i++)
        {
            var (argTemp, argType) = LowerExpr(args[i]);
            argTemps.Add(argTemp);
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

            currentType = Prune(fun.Ret);
        }

        int resultTemp = EmitPerform(effectSym, qv.Name, argTemps);
        return (resultTemp, currentType);
    }

    /// <summary>
    /// Emits the runtime for a perform site: load the effect's innermost handler frame, swap every
    /// handler-evidence global to the frame's snapshot (the arm runs under the evidence in scope at
    /// its handler's installation, with the handler itself removed), call the arm closure with the
    /// operation's arguments, and restore the globals. Typing makes an absent handler unreachable;
    /// a guard panics with a clear message rather than dereferencing null if that invariant is ever
    /// broken.
    /// </summary>
    private int EmitPerform(EffectSymbol effectSym, string opName, List<int> argTemps)
    {
        int effectIndex = _effectIndices[effectSym.Name];
        int opIndex = OperationDeclIndex(effectSym, opName);
        int globalCount = EffectGlobalCount;
        int siteId = _nextEffectSiteId++;
        string unhandledLabel = $"effect_unhandled_{siteId}";
        string doneLabel = $"effect_done_{siteId}";

        int frameTemp = NewTemp();
        Emit(new IrInst.LoadEffectHandler(frameTemp, effectIndex));
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int installedTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(installedTemp, frameTemp, zeroTemp));
        Emit(new IrInst.JumpIfFalse(installedTemp, unhandledLabel));

        // Save the current evidence and switch to the handler frame's snapshot.
        var savedTemps = new int[globalCount];
        for (int k = 0; k < globalCount; k++)
        {
            savedTemps[k] = NewTemp();
            Emit(new IrInst.LoadEffectHandler(savedTemps[k], k));
        }

        for (int k = 0; k < globalCount; k++)
        {
            int snapshotTemp = NewTemp();
            Emit(new IrInst.LoadMemOffset(snapshotTemp, frameTemp, k * 8));
            Emit(new IrInst.StoreEffectHandler(k, snapshotTemp));
        }

        int closureTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(closureTemp, frameTemp, (globalCount + opIndex) * 8));
        int currentTemp = closureTemp;
        foreach (var argTemp in argTemps)
        {
            int callTarget = NewTemp();
            Emit(new IrInst.CallClosure(callTarget, currentTemp, argTemp));
            currentTemp = callTarget;
        }

        // Store into a dedicated result temp before restoring, so the value read after the join
        // label is the arm's result regardless of the argument count.
        for (int k = 0; k < globalCount; k++)
        {
            Emit(new IrInst.StoreEffectHandler(k, savedTemps[k]));
        }

        int resultTemp = NewTemp();
        int resultSlot = NewLocal();
        Emit(new IrInst.StoreLocal(resultSlot, currentTemp));
        Emit(new IrInst.Jump(doneLabel));

        Emit(new IrInst.Label(unhandledLabel));
        var panicLabelStr = InternString($"Unhandled effect operation '{effectSym.Name}.{opName}'.");
        int panicMsgTemp = NewTemp();
        Emit(new IrInst.LoadConstStr(panicMsgTemp, panicLabelStr));
        Emit(new IrInst.PanicStr(panicMsgTemp));

        Emit(new IrInst.Label(doneLabel));
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return resultTemp;
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
        using var handleSpan = PushDiagnosticSpan(GetSpan(handle));

        // 1. Validate and group the arms.
        var opArms = new List<(EffectSymbol Effect, string OpName, HandlerArm Arm)>();
        HandlerArm? returnArm = null;
        bool malformed = false;
        foreach (var arm in handle.Arms)
        {
            if (arm.EffectName is null)
            {
                // The parser only produces a null effect for the `return` arm (anything else was a
                // parse error already).
                if (returnArm is not null)
                {
                    ReportDiagnostic(GetSpan(handle), "Duplicate 'return' arm in handler.", InvalidHandlerCode);
                    malformed = true;
                    continue;
                }

                if (arm.Parameters.Count != 1)
                {
                    ReportDiagnostic(GetSpan(handle), "The 'return' arm takes exactly one parameter.", InvalidHandlerCode);
                    malformed = true;
                    continue;
                }

                returnArm = arm;
                continue;
            }

            if (!_effectSymbols.TryGetValue(arm.EffectName, out var armEffect)
                || !armEffect.Operations.ContainsKey(arm.OperationName))
            {
                ReportDiagnostic(GetSpan(handle), $"Handler arm '{arm.EffectName}.{arm.OperationName}' does not name a declared effect operation.", InvalidHandlerCode);
                malformed = true;
                continue;
            }

            if (opArms.Any(x => ReferenceEquals(x.Effect, armEffect) && string.Equals(x.OpName, arm.OperationName, StringComparison.Ordinal)))
            {
                ReportDiagnostic(GetSpan(handle), $"Duplicate handler arm for '{arm.EffectName}.{arm.OperationName}'.", InvalidHandlerCode);
                malformed = true;
                continue;
            }

            opArms.Add((armEffect, arm.OperationName, arm));
        }

        // A handler discharges whole effects, so every operation of each handled effect needs an arm.
        var handledEffects = opArms.Select(x => x.Effect).Distinct().ToList();
        foreach (var effect in handledEffects)
        {
            foreach (var operation in effect.DeclaringSyntax.Operations)
            {
                if (!opArms.Any(x => ReferenceEquals(x.Effect, effect) && string.Equals(x.OpName, operation.Name, StringComparison.Ordinal)))
                {
                    ReportDiagnostic(GetSpan(handle), $"Handler for effect '{effect.Name}' must handle operation '{operation.Name}'.", InvalidHandlerCode);
                    malformed = true;
                }
            }
        }

        if (malformed || handledEffects.Count == 0)
        {
            if (handledEffects.Count == 0 && !malformed)
            {
                ReportDiagnostic(GetSpan(handle), "A handler must have at least one operation arm.", InvalidHandlerCode);
            }

            return ReturnNeverWithDummyTemp();
        }

        // 2. One instance of each handled effect's type arguments, shared by the body's row entry
        // and every arm's operation signature (`handle ... with | State.get ...` handles State(a)
        // at one concrete-or-inferred `a`).
        var effectInstances = handledEffects.ToDictionary(
            e => e.Name,
            e => e.TypeParameters.Select(_ => NewTypeVar()).ToList(),
            StringComparer.Ordinal);

        // 3. Lower the operation arms to closures (in the enclosing context: an arm belongs
        // lexically outside the handle and its effects flow to the enclosing row).
        int globalCount = EffectGlobalCount;
        var armClosures = new List<(EffectSymbol Effect, int OpIndex, int ClosureTemp)>();
        foreach (var (effect, opName, arm) in opArms)
        {
            var armLambda = BuildOperationArmLambda(effect, opName, arm);
            if (armLambda is null)
            {
                return ReturnNeverWithDummyTemp();
            }

            var (closureTemp, closureType) = LowerExpr(armLambda);
            UnifyArmWithOperation(effect, opName, effectInstances[effect.Name], closureType, arm.Parameters.Count);
            SubsumeCalleeRow(InnermostArrowRow(closureType, arm.Parameters.Count), GetSpan(handle));
            armClosures.Add((effect, OperationDeclIndex(effect, opName), closureTemp));
        }

        // 4. Build and install one handler frame per handled effect:
        // [0 .. globals-1]           snapshot of every handler-evidence global (taken before any of
        //                            this handle's frames install, so arms run under the evidence
        //                            in scope at installation, minus this handler)
        // [globals + opDeclIndex]    the arm closure per operation.
        var frameTemps = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var effect in handledEffects)
        {
            int frameTemp = NewTemp();
            Emit(new IrInst.AllocStack(frameTemp, (globalCount + effect.DeclaringSyntax.Operations.Count) * 8));
            for (int k = 0; k < globalCount; k++)
            {
                int snapshotTemp = NewTemp();
                Emit(new IrInst.LoadEffectHandler(snapshotTemp, k));
                Emit(new IrInst.StoreMemOffset(frameTemp, k * 8, snapshotTemp));
            }

            foreach (var (armEffect, opIndex, closureTemp) in armClosures)
            {
                if (ReferenceEquals(armEffect, effect))
                {
                    Emit(new IrInst.StoreMemOffset(frameTemp, (globalCount + opIndex) * 8, closureTemp));
                }
            }

            frameTemps[effect.Name] = frameTemp;
        }

        foreach (var effect in handledEffects)
        {
            Emit(new IrInst.StoreEffectHandler(_effectIndices[effect.Name], frameTemps[effect.Name]));
        }

        // 5. Lower the body under a row that has the handled effects discharged: anything else it
        // performs flows through the fresh tail to the enclosing row.
        var outerTail = NewTypeVar();
        var bodyRow = new TypeRef.TRow(
            handledEffects.Select(e => new TypeRef.TEffect(e, effectInstances[e.Name])).ToList(),
            outerTail);
        var savedAmbientRow = _ambientRow;
        _ambientRow = bodyRow;
        var (bodyTemp, bodyType) = LowerExpr(handle.Body);
        _ambientRow = savedAmbientRow;
        UnifyRows(outerTail, AmbientRow);

        // 6. Uninstall: restore each handled effect's global from this frame's own snapshot slot.
        foreach (var effect in handledEffects)
        {
            int effectIndex = _effectIndices[effect.Name];
            int previousTemp = NewTemp();
            Emit(new IrInst.LoadMemOffset(previousTemp, frameTemps[effect.Name], effectIndex * 8));
            Emit(new IrInst.StoreEffectHandler(effectIndex, previousTemp));
        }

        // 7. The return arm transforms the body's final value; without one the value passes through.
        if (returnArm is null)
        {
            return (bodyTemp, bodyType);
        }

        int resultSlot = NewLocal();
        Emit(new IrInst.StoreLocal(resultSlot, bodyTemp));
        var resultName = $"__handle_result_{_nextEffectSiteId++}";
        _scopes.Push(new Dictionary<string, Binding>(StringComparer.Ordinal)
        {
            [resultName] = new Binding.Local(resultSlot, bodyType),
        });
        var scrutinee = new Expr.Var(resultName);
        AstSpans.Set(scrutinee, GetSpan(returnArm.Body));
        var returnMatch = new Expr.Match(scrutinee, [new MatchCase(returnArm.Parameters[0], returnArm.Body)], GetSpan(handle).Start);
        AstSpans.Set(returnMatch, GetSpan(returnArm.Body));
        var (resultTemp, resultType) = LowerExpr(returnMatch);
        _scopes.Pop();
        return (resultTemp, resultType);
    }

    /// <summary>
    /// Builds the closure for a tail-resumptive operation arm: parameters become lambda
    /// parameters (complex patterns via a synthesized match), and every tail `resume(e)` is
    /// rewritten to `e` — for a tail-resumptive arm, "resume with v" is exactly "return v to the
    /// perform site". Returns null (with a diagnostic) when the arm is not tail-resumptive.
    /// </summary>
    private Expr? BuildOperationArmLambda(EffectSymbol effect, string opName, HandlerArm arm)
    {
        if (!TryRewriteTailResume(arm.Body, out var body, out var error))
        {
            ReportDiagnostic(GetSpan(arm.Body), $"In handler arm '{effect.Name}.{opName}': {error}", InvalidHandlerCode);
            return null;
        }

        // Parameter patterns: plain variables bind directly; anything else binds a fresh name and
        // matches on it inside the lambda.
        var paramNames = new string[arm.Parameters.Count];
        for (int i = 0; i < arm.Parameters.Count; i++)
        {
            paramNames[i] = arm.Parameters[i] switch
            {
                Pattern.Var v => v.Name,
                _ => $"__arm_arg_{_nextEffectSiteId++}",
            };
        }

        for (int i = arm.Parameters.Count - 1; i >= 0; i--)
        {
            if (arm.Parameters[i] is Pattern.Var or Pattern.Wildcard)
            {
                continue;
            }

            var scrutinee = new Expr.Var(paramNames[i]);
            AstSpans.Set(scrutinee, GetSpan(arm.Parameters[i]));
            var match = new Expr.Match(scrutinee, [new MatchCase(arm.Parameters[i], body)], GetSpan(arm.Parameters[i]).Start);
            AstSpans.Set(match, GetSpan(arm.Body));
            body = match;
        }

        for (int i = arm.Parameters.Count - 1; i >= 0; i--)
        {
            var lambda = new Expr.Lambda(paramNames[i], body);
            AstSpans.Set(lambda, GetSpan(arm.Body));
            body = lambda;
        }

        return body;
    }

    /// <summary>
    /// Rewrites every tail-position <c>resume(e)</c> to <c>e</c>. Fails (with a message) when
    /// <c>resume</c> is used outside tail position — a one-shot resumptive arm, not yet supported —
    /// or when some tail path does not resume — an aborting arm, which needs unwinding.
    /// </summary>
    private bool TryRewriteTailResume(Expr expr, out Expr rewritten, out string error)
    {
        rewritten = expr;
        error = "";
        switch (expr)
        {
            case Expr.Call { Func: Expr.Var { Name: "resume" } } call:
                if (ExprReferencesName(call.Arg, "resume", shadowed: false))
                {
                    error = "'resume' must be called in tail position (one-shot resumptive handlers are not yet supported).";
                    return false;
                }

                rewritten = call.Arg;
                return true;

            case Expr.Let let:
                if (ExprReferencesName(let.Value, "resume", shadowed: false))
                {
                    error = "'resume' must be called in tail position (one-shot resumptive handlers are not yet supported).";
                    return false;
                }

                if (!TryRewriteTailResume(let.Body, out var letBody, out error))
                {
                    return false;
                }

                rewritten = CopySpan(let, let with { Body = letBody });
                return true;

            case Expr.LetRec letRec:
                if (ExprReferencesName(letRec.Value, "resume", shadowed: false))
                {
                    error = "'resume' must be called in tail position (one-shot resumptive handlers are not yet supported).";
                    return false;
                }

                if (!TryRewriteTailResume(letRec.Body, out var letRecBody, out error))
                {
                    return false;
                }

                rewritten = CopySpan(letRec, letRec with { Body = letRecBody });
                return true;

            case Expr.If iff:
                if (ExprReferencesName(iff.Cond, "resume", shadowed: false))
                {
                    error = "'resume' must be called in tail position (one-shot resumptive handlers are not yet supported).";
                    return false;
                }

                if (!TryRewriteTailResume(iff.Then, out var thenBody, out error)
                    || !TryRewriteTailResume(iff.Else, out var elseBody, out error))
                {
                    return false;
                }

                rewritten = CopySpan(iff, new Expr.If(iff.Cond, thenBody, elseBody));
                return true;

            case Expr.Match match:
                if (ExprReferencesName(match.Value, "resume", shadowed: false)
                    || match.Cases.Any(c => c.Guard is not null && ExprReferencesName(c.Guard, "resume", shadowed: false)))
                {
                    error = "'resume' must be called in tail position (one-shot resumptive handlers are not yet supported).";
                    return false;
                }

                var cases = new List<MatchCase>(match.Cases.Count);
                foreach (var matchCase in match.Cases)
                {
                    if (!TryRewriteTailResume(matchCase.Body, out var caseBody, out error))
                    {
                        return false;
                    }

                    cases.Add(new MatchCase(matchCase.Pattern, caseBody, matchCase.Guard));
                }

                rewritten = CopySpan(match, new Expr.Match(match.Value, cases, match.Pos));
                return true;

            default:
                error = ExprReferencesName(expr, "resume", shadowed: false)
                    ? "'resume' must be called in tail position (one-shot resumptive handlers are not yet supported)."
                    : "every path of a tail-resumptive operation arm must end in resume(...).";
                return false;
        }
    }

    private static Expr CopySpan(Expr original, Expr copy)
    {
        AstSpans.Set(copy, AstSpans.GetOrDefault(original));
        return copy;
    }

    /// <summary>
    /// Unifies a lowered arm closure against the operation's type. A rewritten tail-resumptive arm
    /// returns exactly what it resumes with, so the arm closure's shape is the operation's own
    /// function type. The arm's effect rows are detached (replaced by fresh variables) first: the
    /// arm's real row belongs to the enclosing context, not to the operation's published type.
    /// </summary>
    private void UnifyArmWithOperation(EffectSymbol effect, string opName, IReadOnlyList<TypeRef> effectArgs, TypeRef armClosureType, int armParamCount)
    {
        var operation = effect.Operations[opName];
        if (operation.DeclaredSignature is not null && CountArrows(operation.DeclaredSignature) < armParamCount)
        {
            ReportDiagnostic(0, $"Handler arm '{effect.Name}.{opName}' has {armParamCount} parameter(s) but the operation takes {CountArrows(operation.DeclaredSignature)}.", InvalidHandlerCode);
            return;
        }

        var opType = operation.DeclaredSignature is not null
            ? InstantiateEffectSignature(operation.DeclaredSignature, effect.TypeParameters, effectArgs)
            : operation.InferredType!;

        using (PushDiagnosticContext($"in handler arm '{effect.Name}.{opName}'"))
        {
            Unify(DetachRows(armClosureType), opType);
        }
    }

    private int CountArrows(TypeRef type)
    {
        int count = 0;
        var current = Prune(type);
        while (current is TypeRef.TFun fun)
        {
            count++;
            current = Prune(fun.Ret);
        }

        return count;
    }

    /// <summary>Copies a type with every arrow's row replaced by a fresh, unconstrained variable.</summary>
    private TypeRef DetachRows(TypeRef type)
    {
        type = Prune(type);
        return type switch
        {
            TypeRef.TFun fun => new TypeRef.TFun(DetachRows(fun.Arg), DetachRows(fun.Ret)) { Row = NewTypeVar() },
            TypeRef.TList list => new TypeRef.TList(DetachRows(list.Element)),
            TypeRef.TTuple tuple => new TypeRef.TTuple(tuple.Elements.Select(DetachRows).ToList()),
            TypeRef.TNamedType named => new TypeRef.TNamedType(named.Symbol, named.TypeArgs.Select(DetachRows).ToList()),
            _ => type,
        };
    }

    /// <summary>The row of the innermost arrow of a curried function type (where the effects fire).</summary>
    private TypeRef? InnermostArrowRow(TypeRef type, int paramCount)
    {
        var current = Prune(type);
        for (int i = 1; i < paramCount && current is TypeRef.TFun outer; i++)
        {
            current = Prune(outer.Ret);
        }

        return current is TypeRef.TFun inner ? inner.Row : null;
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
