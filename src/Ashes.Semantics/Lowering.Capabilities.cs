using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // Capability diagnostic codes. Defined locally because the shared DiagnosticCodes table
    // lives in Ashes.Frontend.
    private const string UnhandledCapabilityCode = "ASH017";
    private const string CapabilityNotPermittedCode = "ASH018";
    private const string UnknownCapabilityCode = "ASH019";
    private const string InvalidHandlerCode = "ASH020";

    // Declared capabilities by name, plus their registration-order indices — the index selects the
    // capability's handler-evidence global and its snapshot slot inside every handler frame.
    private readonly Dictionary<string, CapabilitySymbol> _capabilitySymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _capabilityIndices = new(StringComparer.Ordinal);

    // Unique suffix source for the labels of perform-site unhandled-guard blocks.
    private int _nextEffectSiteId;

    // First perform-site span per capability, giving the unhandled-capability diagnostic (ASH017) a
    // useful location to point at.
    private readonly Dictionary<string, TextSpan> _firstPerformSites = new(StringComparer.Ordinal);

    // The ambient capability row of the code currently being lowered: every operation performed and
    // every effectful call in a scope inserts its capabilities here. Each lambda body gets its own row
    // (the lambda's arrow row); the field holds the entry expression's row otherwise. Created
    // lazily because type-variable numbering starts with the program.
    private TypeRef? _ambientRow;

    private TypeRef AmbientRow => _ambientRow ??= NewTypeVar();

    private void RegisterCapabilityDeclarations(IReadOnlyList<TopLevelItem> items)
    {
        foreach (var item in items.OfType<TopLevelItem.Capability>())
        {
            var decl = item.Decl;
            if (_capabilitySymbols.ContainsKey(decl.Name))
            {
                ReportDiagnostic(GetSpan(decl), $"Duplicate capability name '{decl.Name}'.");
                continue;
            }

            var typeParameters = decl.TypeParameters
                .Select(tp => new TypeParameterSymbol(tp.Name))
                .ToList();

            var operations = new Dictionary<string, CapabilityOperationSymbol>(StringComparer.Ordinal);
            foreach (var operation in decl.Operations)
            {
                if (operations.ContainsKey(operation.Name))
                {
                    ReportDiagnostic(GetSpan(decl), $"Duplicate operation '{operation.Name}' in capability '{decl.Name}'.");
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
                    // capability's parameters, so a polymorphic operation must be annotated.
                    ReportDiagnostic(
                        GetSpan(decl),
                        $"Operation '{operation.Name}' of parameterized capability '{decl.Name}' requires an explicit signature.",
                        UnknownCapabilityCode);
                    inferredType = NewTypeVar();
                }
                else
                {
                    // Unsigned operation: one shared inference variable, unified across every
                    // perform-site (and, in Stage 2, every handler arm) in the compilation unit.
                    inferredType = NewTypeVar();
                }

                operations[operation.Name] = new CapabilityOperationSymbol(operation.Name, declaredSignature, inferredType);
            }

            _capabilitySymbols[decl.Name] = new CapabilitySymbol(decl.Name, typeParameters, operations, decl);
            _capabilityIndices[decl.Name] = _capabilityIndices.Count;
        }
    }

    // ---------------- Static providers (`provide`) ----------------

    private const string DuplicateProviderCode = "ASH026";
    private const string AmbiguousSatisfactionCode = "ASH027";

    /// <summary>A registered static provider: its capability, the concrete instance's type arguments, and each operation's implementation expression.</summary>
    private sealed record ProviderInfo(CapabilitySymbol Capability, IReadOnlyList<TypeRef> TypeArgs, IReadOnlyDictionary<string, Expr> Operations, TextSpan Span);

    // Providers keyed by concrete-instance key ("Clock", "Ord(Str)", ...).
    private readonly Dictionary<string, ProviderInfo> _providers = new(StringComparer.Ordinal);

    // Capabilities lexically handled by an enclosing `handle` at the current lowering point. A
    // capability operation resolves dynamically (handler evidence) when handled here, statically
    // (a matching provider) otherwise; both applicable is an ambiguity error (ASH027).
    private readonly HashSet<string> _lexicallyHandledCapabilities = new(StringComparer.Ordinal);

    private void RegisterProviderDeclarations(IReadOnlyList<TopLevelItem> items)
    {
        foreach (var item in items.OfType<TopLevelItem.Provide>())
        {
            var decl = item.Decl;
            var span = AstSpans.GetOrDefault(decl);
            span = span.Length == 0 ? TextSpan.FromBounds(span.Start, span.Start + 1) : span;

            if (!_capabilitySymbols.TryGetValue(decl.CapabilityName, out var capability))
            {
                ReportDiagnostic(span, $"'provide' refers to unknown capability '{decl.CapabilityName}'.", UnknownCapabilityCode);
                continue;
            }

            if (decl.TypeArgs.Count != capability.TypeParameters.Count)
            {
                ReportDiagnostic(span, $"Capability '{decl.CapabilityName}' expects {capability.TypeParameters.Count} type argument(s) but the provider supplies {decl.TypeArgs.Count}.", UnknownCapabilityCode);
                continue;
            }

            var typeArgs = decl.TypeArgs.Select(ResolveTypeExpr).ToList();
            var key = BuildProviderKey(decl.CapabilityName, typeArgs);

            // Operation-name validation: no duplicates, no unknown ops, all operations provided.
            var ops = new Dictionary<string, Expr>(StringComparer.Ordinal);
            foreach (var binding in decl.Bindings)
            {
                if (!capability.Operations.ContainsKey(binding.OperationName))
                {
                    ReportDiagnostic(span, $"Capability '{decl.CapabilityName}' has no operation '{binding.OperationName}'.", UnknownCapabilityCode);
                    continue;
                }

                if (!ops.TryAdd(binding.OperationName, binding.Implementation))
                {
                    ReportDiagnostic(span, $"Provider for '{key}' supplies operation '{binding.OperationName}' more than once.", DuplicateProviderCode);
                }
            }

            foreach (var opName in capability.Operations.Keys)
            {
                if (!ops.ContainsKey(opName))
                {
                    ReportDiagnostic(span, $"Provider for '{key}' is missing operation '{opName}'.", DuplicateProviderCode);
                }
            }

            if (_providers.ContainsKey(key))
            {
                ReportDiagnostic(span, $"Duplicate provider for '{key}'.", DuplicateProviderCode);
                continue;
            }

            _providers[key] = new ProviderInfo(capability, typeArgs, ops, span);
        }
    }

    /// <summary>The instance key for a capability applied to (pruned) type arguments, e.g. "Clock" or "Ord(Str)".</summary>
    private string BuildProviderKey(string capabilityName, IReadOnlyList<TypeRef> typeArgs)
    {
        return typeArgs.Count == 0
            ? capabilityName
            : $"{capabilityName}({string.Join(", ", typeArgs.Select(t => Pretty(Prune(t))))})";
    }

    /// <summary>
    /// The provider matching a capability instance, or null when the instance is abstract (a type
    /// argument is still a variable — a generic requirement, resolvable only by monomorphization,
    /// which is deferred) or no provider is registered.
    /// </summary>
    private ProviderInfo? ResolveProvider(CapabilitySymbol capability, IReadOnlyList<TypeRef> typeArgs)
    {
        var pruned = typeArgs.Select(Prune).ToList();
        if (pruned.Any(IsAbstractType))
        {
            return null;
        }

        return _providers.TryGetValue(BuildProviderKey(capability.Name, pruned), out var provider) ? provider : null;
    }

    private static bool IsAbstractType(TypeRef type) => type is TypeRef.TVar or TypeRef.TTypeParam;

    /// <summary>Whether any provider is registered for the capability of the given name (any instance).</summary>
    private bool HasAnyProvider(string capabilityName)
    {
        return _providers.Keys.Any(k => k == capabilityName || k.StartsWith($"{capabilityName}(", StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether an expression syntactically calls an operation of a *parameterized* capability that has
    /// a provider — the signal that inlining the enclosing function at a concrete call site would let
    /// that operation resolve statically (capability monomorphization).
    /// </summary>
    private bool BodyPerformsProvidedParameterizedCapability(Expr expr)
    {
        var found = false;

        void Visit(object? node)
        {
            if (found || node is null or string)
            {
                return;
            }

            if (node is Expr.QualifiedVar qv
                && _capabilitySymbols.TryGetValue(qv.Module, out var cap)
                && cap.TypeParameters.Count > 0
                && cap.Operations.ContainsKey(qv.Name)
                && HasAnyProvider(qv.Module))
            {
                found = true;
                return;
            }

            // Walk records (Expr/Pattern/MatchCase) and their collections reflectively so every Expr
            // shape is covered by default, mirroring CountBinders.
            if (node is System.Runtime.CompilerServices.ITuple tuple)
            {
                for (int i = 0; i < tuple.Length && !found; i++)
                {
                    Visit(tuple[i]);
                }

                return;
            }

            if (node is System.Collections.IEnumerable seq)
            {
                foreach (var item in seq)
                {
                    Visit(item);
                    if (found)
                    {
                        return;
                    }
                }

                return;
            }

            if (node is not (Expr or Pattern or MatchCase or HandlerArm))
            {
                return;
            }

            foreach (var prop in node.GetType().GetProperties())
            {
                if (found)
                {
                    return;
                }

                if (prop.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                var t = prop.PropertyType;
                if (typeof(Expr).IsAssignableFrom(t)
                    || typeof(Pattern).IsAssignableFrom(t)
                    || typeof(MatchCase).IsAssignableFrom(t)
                    || typeof(HandlerArm).IsAssignableFrom(t)
                    || (typeof(System.Collections.IEnumerable).IsAssignableFrom(t) && t != typeof(string)))
                {
                    Visit(prop.GetValue(node));
                }
            }
        }

        Visit(expr);
        return found;
    }

    /// <summary>Number of declared capabilities: the backend materializes one handler-evidence global per capability.</summary>
    private int CapabilityGlobalCount => _capabilitySymbols.Count;

    /// <summary>
    /// Index of the pending-post register: one extra global (after the per-capability evidence slots)
    /// through which a one-shot resumptive arm hands its post-resume continuation closure back to
    /// the perform site. Invariant: 0 except between the arm's store and the perform site's
    /// consume-and-reset, so a tail-resumptive arm pushes nothing.
    /// </summary>
    private int PostRegisterIndex => CapabilityGlobalCount;

    /// <summary>
    /// Index of the live-posts counter global: the number of collected post-resume continuations
    /// not yet folded by their handle. While it is non-zero, every arena restore/reclaim is
    /// skipped — a pending post (and everything it captures) lives in arena allocations that must
    /// survive until the handle folds it. Data a post references always predates its push, so any
    /// window with no push during it stays safe to reclaim.
    /// </summary>
    private int LivePostsIndex => CapabilityGlobalCount + 1;

    /// <summary>
    /// Internal-only AST node produced by the one-shot arm rewrite. Evaluates <see cref="Value"/>
    /// (the resume argument, returned to the perform site), then creates <see cref="PostLambda"/>
    /// (the arm's work after resume, as a function of the handle's result) and stores it in the
    /// pending-post register for the perform site to collect. Only ever created inside a
    /// synthesized handler arm, so no generic expression walker encounters it.
    /// </summary>
    internal sealed record CapabilityPostExpr(Expr Value, Expr PostLambda, TypeRef HandleResultType) : Expr;

    /// <summary>
    /// Begins a live-posts guard around an arena restore/copy-out/reclaim block: emits a check
    /// that jumps past the block when any post-resume continuation is pending. Returns the skip
    /// label to place after the block, or null (emitting nothing) when the program declares no
    /// capabilities and the guard is unnecessary.
    /// </summary>
    private string? BeginLivePostsGuard()
    {
        if (CapabilityGlobalCount == 0)
        {
            return null;
        }

        int counterTemp = NewTemp();
        Emit(new IrInst.LoadCapabilityHandler(counterTemp, LivePostsIndex));
        int zeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(zeroTemp, 0));
        int isZeroTemp = NewTemp();
        Emit(new IrInst.CmpIntEq(isZeroTemp, counterTemp, zeroTemp));
        string skipLabel = $"live_posts_skip_{_nextEffectSiteId++}";
        Emit(new IrInst.JumpIfFalse(isZeroTemp, skipLabel));
        return skipLabel;
    }

    private void EndLivePostsGuard(string? skipLabel)
    {
        if (skipLabel is not null)
        {
            Emit(new IrInst.Label(skipLabel));
        }
    }

    /// <summary>Emits `counter := counter + delta` on the live-posts global.</summary>
    private void EmitLivePostsAdjust(long delta)
    {
        int counterTemp = NewTemp();
        Emit(new IrInst.LoadCapabilityHandler(counterTemp, LivePostsIndex));
        int deltaTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(deltaTemp, delta));
        int adjustedTemp = NewTemp();
        Emit(new IrInst.AddInt(adjustedTemp, counterTemp, deltaTemp));
        Emit(new IrInst.StoreCapabilityHandler(LivePostsIndex, adjustedTemp));
    }

    /// <summary>
    /// Lowers the internal one-shot arm node: evaluate the resume argument first (it may itself
    /// perform), then create the post-resume continuation closure and hand it to the perform site
    /// through the pending-post register.
    /// </summary>
    private (int, TypeRef) LowerEffectPost(CapabilityPostExpr post)
    {
        var (valueTemp, valueType) = LowerExpr(post.Value);
        var (postTemp, postType) = LowerExpr(post.PostLambda);

        // The post runs after its handle exits, transforming the handle's result: R -> R. Its
        // capabilities belong to the enclosing context (the ambient row the arm is lowered under).
        if (Prune(postType) is TypeRef.TFun postFun)
        {
            Unify(postFun.Arg, post.HandleResultType);
            Unify(postFun.Ret, post.HandleResultType);
            SubsumeCalleeRow(postFun.Row, GetSpan(post.PostLambda));
        }

        Emit(new IrInst.StoreCapabilityHandler(PostRegisterIndex, postTemp));
        return (valueTemp, valueType);
    }

    /// <summary>The operation's slot index inside a handler frame for its capability (declaration order).</summary>
    private static int OperationDeclIndex(CapabilitySymbol capability, string opName)
    {
        for (int i = 0; i < capability.DeclaringSyntax.Operations.Count; i++)
        {
            if (string.Equals(capability.DeclaringSyntax.Operations[i].Name, opName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static TextSpan GetSpan(CapabilityDecl effectDecl)
    {
        var span = AstSpans.GetOrDefault(effectDecl);
        return span.Length == 0 ? TextSpan.FromBounds(span.Start, span.Start + 1) : span;
    }

    // ---------------- Row normalization and unification ----------------

    /// <summary>
    /// Normalizes a row to its concrete capabilities (deduped by capability name, unifying the type
    /// arguments of duplicates) plus the open tail variable, or null when closed. A null row is
    /// the pure closed empty row.
    /// </summary>
    private (List<TypeRef.TCapability> Capabilities, TypeRef.TVar? Tail) NormalizeRow(TypeRef? row)
    {
        var capabilities = new List<TypeRef.TCapability>();
        TypeRef.TVar? tail = null;

        void Add(TypeRef.TCapability capability)
        {
            foreach (var existing in capabilities)
            {
                if (string.Equals(existing.Symbol.Name, capability.Symbol.Name, StringComparison.Ordinal))
                {
                    // A row contains at most one instance of a capability; a second mention unifies
                    // the instances' type arguments.
                    for (int i = 0; i < Math.Min(existing.Args.Count, capability.Args.Count); i++)
                    {
                        Unify(existing.Args[i], capability.Args[i]);
                    }

                    return;
                }
            }

            capabilities.Add(capability);
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
                    foreach (var capability in tr.Capabilities)
                    {
                        Add(capability);
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
        return (capabilities, tail);
    }

    /// <summary>
    /// Row unification: capabilities present on both sides unify their type arguments; capabilities present
    /// on one side only must be absorbed by the other side's tail variable (a closed side that is
    /// missing a capability is an error). Open tails are re-linked through a shared fresh tail.
    /// </summary>
    private void UnifyRows(TypeRef? a, TypeRef? b)
    {
        var (effectsA, tailA) = NormalizeRow(a);
        var (effectsB, tailB) = NormalizeRow(b);

        var onlyA = new List<TypeRef.TCapability>();
        foreach (var effectA in effectsA)
        {
            TypeRef.TCapability? match = null;
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
            // One tail cannot absorb different capability sets on both sides.
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

    private void ReportRowMissingEffects(List<TypeRef.TCapability> missing, List<TypeRef.TCapability> closedRowEffects)
    {
        var names = string.Join(", ", missing.Select(PrettyEffect).OrderBy(n => n, StringComparer.Ordinal));
        var row = string.Join(", ", closedRowEffects.Select(PrettyEffect).OrderBy(n => n, StringComparer.Ordinal));
        var plural = missing.Count == 1 ? $"Capability '{names}' is" : $"Capabilities '{names}' are";
        ReportDiagnostic(0, $"{plural} not permitted by the closed row needs {{{row}}}.", CapabilityNotPermittedCode);
    }

    private string PrettyEffect(TypeRef.TCapability capability)
    {
        return capability.Args.Count == 0
            ? capability.Symbol.Name
            : $"{capability.Symbol.Name}({string.Join(", ", capability.Args.Select(Pretty))})";
    }

    // ---------------- Ambient-row plumbing ----------------

    /// <summary>
    /// Records that calling a function with row <paramref name="calleeRow"/> performs that row's
    /// capabilities in the current context. An open (inferred) callee row is unified with the ambient
    /// row; a closed (annotated) row only requires its capabilities to be present — calling a
    /// <c>uses {Prices}</c> function from a <c>{Prices, Clock}</c> context is fine.
    /// </summary>
    private void SubsumeCalleeRow(TypeRef? calleeRow, TextSpan span)
    {
        if (calleeRow is null)
        {
            return;
        }

        var (capabilities, tail) = NormalizeRow(calleeRow);
        if (capabilities.Count == 0 && tail is null)
        {
            return;
        }

        foreach (var capability in capabilities)
        {
            RecordPerformSite(capability, span);
        }

        if (tail is not null)
        {
            UnifyRows(calleeRow, AmbientRow);
        }
        else
        {
            RequireEffectsInAmbient(capabilities);
        }
    }

    /// <summary>Requires the ambient row to include the given capabilities, extending its tail as needed.</summary>
    private void RequireEffectsInAmbient(List<TypeRef.TCapability> capabilities)
    {
        if (capabilities.Count == 0)
        {
            return;
        }

        UnifyRows(new TypeRef.TRow(capabilities, NewTypeVar()), AmbientRow);
    }

    private void RecordPerformSite(TypeRef.TCapability capability, TextSpan span)
    {
        if (span.Length > 0 && !_firstPerformSites.ContainsKey(capability.Symbol.Name))
        {
            _firstPerformSites[capability.Symbol.Name] = span;
        }
    }

    /// <summary>
    /// The end-of-program unhandled-capability check (ASH017): after lowering, any concrete capability
    /// left in the entry expression's row has no handler discharging it.
    /// </summary>
    private void CheckUnhandledEffects()
    {
        if (_ambientRow is null)
        {
            return;
        }

        var (capabilities, _) = NormalizeRow(_ambientRow);
        foreach (var capability in capabilities.OrderBy(e => e.Symbol.Name, StringComparer.Ordinal))
        {
            var span = _firstPerformSites.TryGetValue(capability.Symbol.Name, out var performSite)
                ? performSite
                : default;

            // A provider exists but the requirement survived to the top level: the operation is used
            // at a generic instance the compiler could not monomorphize — inside a *recursive* or a
            // *higher-order* generic function (a capability op in a closure passed to another
            // function). Point at that, rather than the plain "no handler or provider".
            if (HasAnyProvider(capability.Symbol.Name))
            {
                ReportDiagnostic(span, $"Capability '{capability.Symbol.Name}' is used at a generic type here. Annotate the enclosing function with an explicit `needs {{{capability.Symbol.Name}(...)}}` row so it can receive the capability, or call it at a concrete type / install a handler.", CapabilityNotPermittedCode);
                continue;
            }

            ReportDiagnostic(span, $"Unsatisfied capability '{capability.Symbol.Name}': no handler or provider satisfies it.", UnhandledCapabilityCode);
        }
    }

    // ---------------- Perform / operation calls / handle ----------------

    private (int, TypeRef) LowerPerform(Expr.Perform perform)
    {
        // `perform` is an optional no-op marker; it must wrap a capability operation call.
        var collectedArgs = new List<Expr>();
        var rootExpr = CollectCallArgs(perform.Operation, collectedArgs);
        if (rootExpr is Expr.QualifiedVar qv
            && _capabilitySymbols.TryGetValue(qv.Module, out var effectSym)
            && collectedArgs.Count > 0)
        {
            return LowerCapabilityOperationCall(effectSym, qv, collectedArgs);
        }

        ReportDiagnostic(GetSpan(perform), "'perform' must be applied to a capability operation call.", UnknownCapabilityCode);
        return LowerExpr(perform.Operation);
    }

    private (int, TypeRef) LowerCapabilityOperationCall(CapabilitySymbol effectSym, Expr.QualifiedVar qv, List<Expr> args)
    {
        var span = GetSpan(qv);
        if (!effectSym.Operations.TryGetValue(qv.Name, out var operation))
        {
            ReportDiagnostic(span, $"Capability '{effectSym.Name}' has no operation '{qv.Name}'.", UnknownCapabilityCode);
            foreach (var arg in args)
            {
                LowerExpr(arg);
            }

            return ReturnNeverWithDummyTemp();
        }

        // Instantiate the operation's type. A declared signature replaces the capability's type
        // parameters with fresh variables shared with the row entry, so `State(a)` ties
        // `get : Unit -> a` to the `State(a)` instance in the row. An unsigned operation uses its
        // shared inference variable (monomorphic within the compilation unit).
        var effectArgs = effectSym.TypeParameters.Select(_ => NewTypeVar()).ToList();
        var opType = operation.DeclaredSignature is not null
            ? InstantiateEffectSignature(operation.DeclaredSignature, effectSym.TypeParameters, effectArgs)
            : operation.InferredType!;

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

            if (currentType is not TypeRef.TFun funType)
            {
                ReportDiagnostic(span, $"Operation '{effectSym.Name}.{qv.Name}' expects {i} argument(s) but got {args.Count}.");
                return ReturnNeverWithDummyTemp();
            }

            using (PushDiagnosticContext($"in argument #{i + 1} of operation '{effectSym.Name}.{qv.Name}'"))
            {
                Unify(funType.Arg, argType);
            }

            currentType = Prune(funType.Ret);
        }

        // Decide how the requirement is satisfied now that the instance's type arguments are pinned:
        //  - lexically handled by an enclosing `handle`  -> dynamic evidence path (row-tracked)
        //  - a matching static provider, not handled     -> direct call to the provider's impl
        //  - both                                         -> ambiguity error (ASH027)
        //  - neither / abstract instance                 -> dynamic path; residual row -> ASH017
        bool handled = _lexicallyHandledCapabilities.Contains(effectSym.Name);
        var provider = ResolveProvider(effectSym, effectArgs);

        if (handled && provider is not null)
        {
            ReportDiagnostic(span, $"Capability '{effectSym.Name}' is satisfied both by a provider and by an enclosing handler. Choose one.", AmbiguousSatisfactionCode);
        }

        if (provider is not null && !handled)
        {
            int staticResult = EmitStaticProviderCall(provider, qv.Name, argTemps, span);
            return (staticResult, currentType);
        }

        // An abstract instance (a type variable) can't resolve to a provider here. When this call is
        // inside a capability-generic function, the enclosing function is inlined at each concrete
        // call site (capability monomorphization), and this eager lowering is the dead dynamic
        // fallback — it stays correct (a handler still satisfies it) and the inlined copies resolve
        // statically. So fall through to the dynamic path rather than erroring.

        // Dynamic satisfaction: record the requirement in the ambient row so a handler discharges
        // it (or the top-level unsatisfied check reports it), and emit the evidence-based perform.
        var effectInstance = new TypeRef.TCapability(effectSym, effectArgs);
        RecordPerformSite(effectInstance, span);
        using (PushDiagnosticSpan(span))
        {
            RequireEffectsInAmbient([effectInstance]);
        }

        int resultTemp = EmitPerform(effectSym, qv.Name, argTemps);
        return (resultTemp, currentType);
    }

    /// <summary>
    /// Emits a static provider resolution: lowers the provider's operation implementation (a normal
    /// expression, e.g. <c>Ashes.String.compare</c> or a lambda), unifies it against the operation's
    /// instantiated signature (so a wrong-typed implementation is a type error), and applies the
    /// operation's arguments as an ordinary curried call. No handler evidence is involved.
    /// </summary>
    private int EmitStaticProviderCall(ProviderInfo provider, string opName, List<int> argTemps, TextSpan span)
    {
        var impl = provider.Operations[opName];
        var (implTemp, implType) = LowerExpr(impl);

        // Type-check: the implementation must have the operation's signature at this concrete instance.
        var operation = provider.Capability.Operations[opName];
        if (operation.DeclaredSignature is not null)
        {
            var expected = InstantiateEffectSignature(operation.DeclaredSignature, provider.Capability.TypeParameters, provider.TypeArgs);
            using (PushDiagnosticContext($"in provider '{BuildProviderKey(provider.Capability.Name, provider.TypeArgs)}' operation '{opName}'"))
            {
                Unify(implType, expected);
            }
        }

        int current = implTemp;
        foreach (var argTemp in argTemps)
        {
            int callTarget = NewTemp();
            Emit(new IrInst.CallClosure(callTarget, current, argTemp));
            current = callTarget;
        }

        return current;
    }

    /// <summary>
    /// Emits the runtime for a perform site: load the capability's innermost handler frame, swap every
    /// handler-evidence global to the frame's snapshot (the arm runs under the evidence in scope at
    /// its handler's installation, with the handler itself removed), call the arm closure with the
    /// operation's arguments, and restore the globals. Typing makes an absent handler unreachable;
    /// a guard panics with a clear message rather than dereferencing null if that invariant is ever
    /// broken.
    /// </summary>
    private int EmitPerform(CapabilitySymbol effectSym, string opName, List<int> argTemps)
    {
        int effectIndex = _capabilityIndices[effectSym.Name];
        int opIndex = OperationDeclIndex(effectSym, opName);
        int globalCount = CapabilityGlobalCount;
        int siteId = _nextEffectSiteId++;
        string unhandledLabel = $"effect_unhandled_{siteId}";
        string doneLabel = $"effect_done_{siteId}";

        int frameTemp = NewTemp();
        Emit(new IrInst.LoadCapabilityHandler(frameTemp, effectIndex));
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
            Emit(new IrInst.LoadCapabilityHandler(savedTemps[k], k));
        }

        for (int k = 0; k < globalCount; k++)
        {
            int snapshotTemp = NewTemp();
            Emit(new IrInst.LoadMemOffset(snapshotTemp, frameTemp, k * 8));
            Emit(new IrInst.StoreCapabilityHandler(k, snapshotTemp));
        }

        int closureTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(closureTemp, frameTemp, (globalCount + 1 + opIndex) * 8));
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
            Emit(new IrInst.StoreCapabilityHandler(k, savedTemps[k]));
        }

        // Collect a one-shot arm's post-resume continuation: consume-and-reset the pending-post
        // register, and push a {closure, next} cell onto the handle's shared LIFO posts list
        // (frame slot globalCount holds a pointer to the list head slot). Tail-resumptive arms
        // never store the register, so this is a load-compare-skip for them.
        int postTemp = NewTemp();
        Emit(new IrInst.LoadCapabilityHandler(postTemp, PostRegisterIndex));
        int postZeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(postZeroTemp, 0));
        Emit(new IrInst.StoreCapabilityHandler(PostRegisterIndex, postZeroTemp));
        int hasPostTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(hasPostTemp, postTemp, postZeroTemp));
        string noPostLabel = $"effect_no_post_{siteId}";
        Emit(new IrInst.JumpIfFalse(hasPostTemp, noPostLabel));
        int postsHeadPtrTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(postsHeadPtrTemp, frameTemp, globalCount * 8));
        int cellTemp = NewTemp();
        Emit(new IrInst.Alloc(cellTemp, 16));
        Emit(new IrInst.StoreMemOffset(cellTemp, 0, postTemp));
        int previousHeadTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(previousHeadTemp, postsHeadPtrTemp, 0));
        Emit(new IrInst.StoreMemOffset(cellTemp, 8, previousHeadTemp));
        Emit(new IrInst.StoreMemOffset(postsHeadPtrTemp, 0, cellTemp));
        EmitLivePostsAdjust(1);
        Emit(new IrInst.Label(noPostLabel));

        int resultTemp = NewTemp();
        int resultSlot = NewLocal();
        Emit(new IrInst.StoreLocal(resultSlot, currentTemp));
        Emit(new IrInst.Jump(doneLabel));

        Emit(new IrInst.Label(unhandledLabel));
        var panicLabelStr = InternString($"Unhandled capability operation '{effectSym.Name}.{opName}'.");
        int panicMsgTemp = NewTemp();
        Emit(new IrInst.LoadConstStr(panicMsgTemp, panicLabelStr));
        Emit(new IrInst.PanicStr(panicMsgTemp));

        Emit(new IrInst.Label(doneLabel));
        Emit(new IrInst.LoadLocal(resultTemp, resultSlot));
        return resultTemp;
    }

    /// <summary>Substitutes a capability's type parameters with fresh per-use variables in an operation signature.</summary>
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
                        row.Capabilities.Select(e => new TypeRef.TCapability(e.Symbol, e.Args.Select(Walk).ToList())).ToList(),
                        row.Tail is null ? null : Walk(row.Tail));
                case TypeRef.TCapability capability:
                    return new TypeRef.TCapability(capability.Symbol, capability.Args.Select(Walk).ToList());
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
        var opArms = new List<(CapabilitySymbol Capability, string OpName, HandlerArm Arm)>();
        HandlerArm? returnArm = null;
        bool malformed = false;
        foreach (var arm in handle.Arms)
        {
            if (arm.CapabilityName is null)
            {
                // The parser only produces a null capability for the `return` arm (anything else was a
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

            if (!_capabilitySymbols.TryGetValue(arm.CapabilityName, out var armEffect)
                || !armEffect.Operations.ContainsKey(arm.OperationName))
            {
                ReportDiagnostic(GetSpan(handle), $"Handler arm '{arm.CapabilityName}.{arm.OperationName}' does not name a declared capability operation.", InvalidHandlerCode);
                malformed = true;
                continue;
            }

            if (opArms.Any(x => ReferenceEquals(x.Capability, armEffect) && string.Equals(x.OpName, arm.OperationName, StringComparison.Ordinal)))
            {
                ReportDiagnostic(GetSpan(handle), $"Duplicate handler arm for '{arm.CapabilityName}.{arm.OperationName}'.", InvalidHandlerCode);
                malformed = true;
                continue;
            }

            opArms.Add((armEffect, arm.OperationName, arm));
        }

        // A handler discharges whole capabilities, so every operation of each handled capability needs an arm.
        var handledEffects = opArms.Select(x => x.Capability).Distinct().ToList();
        foreach (var capability in handledEffects)
        {
            foreach (var operation in capability.DeclaringSyntax.Operations)
            {
                if (!opArms.Any(x => ReferenceEquals(x.Capability, capability) && string.Equals(x.OpName, operation.Name, StringComparison.Ordinal)))
                {
                    ReportDiagnostic(GetSpan(handle), $"Handler for capability '{capability.Name}' must handle operation '{operation.Name}'.", InvalidHandlerCode);
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

        // 2. One instance of each handled capability's type arguments, shared by the body's row entry
        // and every arm's operation signature (`handle ... with | State.get ...` handles State(a)
        // at one concrete-or-inferred `a`), plus the handle's result type — one-shot arms need it
        // before the body is lowered, since their post-resume continuations have type R -> R.
        var effectInstances = handledEffects.ToDictionary(
            e => e.Name,
            e => e.TypeParameters.Select(_ => NewTypeVar()).ToList(),
            StringComparer.Ordinal);
        var resultType = NewTypeVar();

        // 3. Lower the operation arms to closures (in the enclosing context: an arm belongs
        // lexically outside the handle and its capabilities flow to the enclosing row).
        int globalCount = CapabilityGlobalCount;
        var armClosures = new List<(CapabilitySymbol Capability, int OpIndex, int ClosureTemp)>();
        foreach (var (capability, opName, arm) in opArms)
        {
            var armLambda = BuildOperationArmLambda(capability, opName, arm, resultType);
            if (armLambda is null)
            {
                return ReturnNeverWithDummyTemp();
            }

            var (closureTemp, closureType) = LowerExpr(armLambda);
            UnifyArmWithOperation(capability, opName, effectInstances[capability.Name], closureType, arm.Parameters.Count);
            SubsumeCalleeRow(InnermostArrowRow(closureType, arm.Parameters.Count), GetSpan(handle));
            armClosures.Add((capability, OperationDeclIndex(capability, opName), closureTemp));
        }

        // 4. Build and install one handler frame per handled capability:
        // [0 .. globals-1]              snapshot of every handler-evidence global (taken before
        //                               any of this handle's frames install, so arms run under the
        //                               evidence in scope at installation, minus this handler)
        // [globals]                     pointer to the handle's shared posts-list head slot
        // [globals + 1 + opDeclIndex]   the arm closure per operation.
        int postsHeadPtrTemp = NewTemp();
        Emit(new IrInst.AllocStack(postsHeadPtrTemp, 8));
        int postsInitZeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(postsInitZeroTemp, 0));
        Emit(new IrInst.StoreMemOffset(postsHeadPtrTemp, 0, postsInitZeroTemp));

        var frameTemps = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var capability in handledEffects)
        {
            int frameTemp = NewTemp();
            Emit(new IrInst.AllocStack(frameTemp, (globalCount + 1 + capability.DeclaringSyntax.Operations.Count) * 8));
            for (int k = 0; k < globalCount; k++)
            {
                int snapshotTemp = NewTemp();
                Emit(new IrInst.LoadCapabilityHandler(snapshotTemp, k));
                Emit(new IrInst.StoreMemOffset(frameTemp, k * 8, snapshotTemp));
            }

            Emit(new IrInst.StoreMemOffset(frameTemp, globalCount * 8, postsHeadPtrTemp));
            foreach (var (armEffect, opIndex, closureTemp) in armClosures)
            {
                if (ReferenceEquals(armEffect, capability))
                {
                    Emit(new IrInst.StoreMemOffset(frameTemp, (globalCount + 1 + opIndex) * 8, closureTemp));
                }
            }

            frameTemps[capability.Name] = frameTemp;
        }

        foreach (var capability in handledEffects)
        {
            Emit(new IrInst.StoreCapabilityHandler(_capabilityIndices[capability.Name], frameTemps[capability.Name]));
        }

        // 5. Lower the body under a row that has the handled capabilities discharged: anything else it
        // performs flows through the fresh tail to the enclosing row.
        var outerTail = NewTypeVar();
        var bodyRow = new TypeRef.TRow(
            handledEffects.Select(e => new TypeRef.TCapability(e, effectInstances[e.Name])).ToList(),
            outerTail);
        var savedAmbientRow = _ambientRow;
        _ambientRow = bodyRow;
        // Operations of the handled capabilities resolve dynamically (this handler's evidence) while
        // lowering the body — and a static provider for one of them is then an ambiguity error.
        var newlyHandled = handledEffects.Where(e => _lexicallyHandledCapabilities.Add(e.Name)).ToList();
        var (bodyTemp, bodyType) = LowerExpr(handle.Body);
        foreach (var handledCapability in newlyHandled)
        {
            _lexicallyHandledCapabilities.Remove(handledCapability.Name);
        }

        _ambientRow = savedAmbientRow;
        UnifyRows(outerTail, AmbientRow);

        // 6. Uninstall: restore each handled capability's global from this frame's own snapshot slot.
        foreach (var capability in handledEffects)
        {
            int effectIndex = _capabilityIndices[capability.Name];
            int previousTemp = NewTemp();
            Emit(new IrInst.LoadMemOffset(previousTemp, frameTemps[capability.Name], effectIndex * 8));
            Emit(new IrInst.StoreCapabilityHandler(effectIndex, previousTemp));
        }

        // 7. The return arm transforms the body's final value; without one the value passes through.
        int currentResultTemp;
        if (returnArm is null)
        {
            Unify(resultType, bodyType);
            currentResultTemp = bodyTemp;
        }
        else
        {
            int bodySlot = NewLocal();
            Emit(new IrInst.StoreLocal(bodySlot, bodyTemp));
            var resultName = $"__handle_result_{_nextEffectSiteId++}";
            _scopes.Push(new Dictionary<string, Binding>(StringComparer.Ordinal)
            {
                [resultName] = new Binding.Local(bodySlot, bodyType),
            });
            var scrutinee = new Expr.Var(resultName);
            AstSpans.Set(scrutinee, GetSpan(returnArm.Body));
            var returnMatch = new Expr.Match(scrutinee, [new MatchCase(returnArm.Parameters[0], returnArm.Body)], GetSpan(handle).Start);
            AstSpans.Set(returnMatch, GetSpan(returnArm.Body));
            var (returnTemp, returnType) = LowerExpr(returnMatch);
            _scopes.Pop();
            Unify(resultType, returnType);
            currentResultTemp = returnTemp;
        }

        // 8. Fold the collected one-shot post-resume continuations over the result, LIFO (the
        // most recent perform's continuation is innermost in the reduction), decrementing the
        // live-posts counter as each is consumed. Posts run here — outside the handle — under the
        // enclosing evidence, matching the deep-handler reduction C[handle E[v] with h].
        int foldId = _nextEffectSiteId++;
        string foldLoopLabel = $"posts_fold_{foldId}";
        string foldDoneLabel = $"posts_fold_done_{foldId}";
        int foldResultSlot = NewLocal();
        Emit(new IrInst.StoreLocal(foldResultSlot, currentResultTemp));
        int foldHeadSlot = NewLocal();
        int initialHeadTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(initialHeadTemp, postsHeadPtrTemp, 0));
        Emit(new IrInst.StoreLocal(foldHeadSlot, initialHeadTemp));
        Emit(new IrInst.Label(foldLoopLabel));
        int headTemp = NewTemp();
        Emit(new IrInst.LoadLocal(headTemp, foldHeadSlot));
        int foldZeroTemp = NewTemp();
        Emit(new IrInst.LoadConstInt(foldZeroTemp, 0));
        int hasCellTemp = NewTemp();
        Emit(new IrInst.CmpIntNe(hasCellTemp, headTemp, foldZeroTemp));
        Emit(new IrInst.JumpIfFalse(hasCellTemp, foldDoneLabel));
        int postClosureTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(postClosureTemp, headTemp, 0));
        int foldInTemp = NewTemp();
        Emit(new IrInst.LoadLocal(foldInTemp, foldResultSlot));
        int foldOutTemp = NewTemp();
        Emit(new IrInst.CallClosure(foldOutTemp, postClosureTemp, foldInTemp));
        Emit(new IrInst.StoreLocal(foldResultSlot, foldOutTemp));
        int nextCellTemp = NewTemp();
        Emit(new IrInst.LoadMemOffset(nextCellTemp, headTemp, 8));
        Emit(new IrInst.StoreLocal(foldHeadSlot, nextCellTemp));
        EmitLivePostsAdjust(-1);
        Emit(new IrInst.Jump(foldLoopLabel));
        Emit(new IrInst.Label(foldDoneLabel));
        int finalResultTemp = NewTemp();
        Emit(new IrInst.LoadLocal(finalResultTemp, foldResultSlot));
        return (finalResultTemp, resultType);
    }

    /// <summary>
    /// Builds the closure for an operation arm: parameters become lambda parameters (complex
    /// patterns via a synthesized match), and each `resume` is rewritten away. A tail
    /// `resume(e)` becomes `e` ("resume with v" is exactly "return v to the perform site"); a
    /// one-shot `resume` — the scrutinee of a match or the value of a let, with work after —
    /// splits into the resume argument plus a post-resume continuation handed to the perform
    /// site. Returns null (with a diagnostic) when the arm uses `resume` in an unsupported
    /// position or has a path that never resumes.
    /// </summary>
    private Expr? BuildOperationArmLambda(CapabilitySymbol capability, string opName, HandlerArm arm, TypeRef handleResultType)
    {
        if (!TryRewriteResume(arm.Body, handleResultType, out var body, out var error))
        {
            ReportDiagnostic(GetSpan(arm.Body), $"In handler arm '{capability.Name}.{opName}': {error}", InvalidHandlerCode);
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

    private const string UnsupportedResumePosition =
        "'resume' is only supported in tail position, as the value of a let, or as the scrutinee of a match; bind its result with 'let' to move the work after it.";

    /// <summary>
    /// Rewrites the arm's <c>resume</c> calls away. On each execution path, <c>resume</c> must be
    /// called exactly once, in one of the supported positions:
    /// tail — <c>resume(e)</c> becomes <c>e</c>;
    /// let value — <c>let x = resume(v) in B</c> splits into <c>v</c> plus post <c>given x -&gt; B</c>;
    /// match scrutinee — <c>match resume(v) with cases</c> splits into <c>v</c> plus
    /// <c>given r -&gt; match r with cases</c>.
    /// A path that never resumes is an aborting arm (needs unwinding) and is rejected.
    /// </summary>
    private bool TryRewriteResume(Expr expr, TypeRef handleResultType, out Expr rewritten, out string error)
    {
        rewritten = expr;
        error = "";
        switch (expr)
        {
            case Expr.Call { Func: Expr.Var { Name: "resume" } } call:
                if (ExprReferencesName(call.Arg, "resume", shadowed: false))
                {
                    error = UnsupportedResumePosition;
                    return false;
                }

                rewritten = call.Arg;
                return true;

            case Expr.Let { Value: Expr.Call { Func: Expr.Var { Name: "resume" } } resumeCall } oneShotLet:
                if (ExprReferencesName(resumeCall.Arg, "resume", shadowed: false)
                    || ExprReferencesName(oneShotLet.Body, "resume", shadowed: false))
                {
                    error = "'resume' may run at most once per path (multi-shot handlers are out of scope).";
                    return false;
                }

                rewritten = BuildEffectPost(oneShotLet.Name, oneShotLet.Body, resumeCall.Arg, handleResultType, oneShotLet);
                return true;

            case Expr.Let let:
                if (ExprReferencesName(let.Value, "resume", shadowed: false))
                {
                    error = UnsupportedResumePosition;
                    return false;
                }

                if (!TryRewriteResume(let.Body, handleResultType, out var letBody, out error))
                {
                    return false;
                }

                rewritten = CopySpan(let, let with { Body = letBody });
                return true;

            case Expr.LetRecursive letRecursive:
                if (ExprReferencesName(letRecursive.Value, "resume", shadowed: false))
                {
                    error = UnsupportedResumePosition;
                    return false;
                }

                if (!TryRewriteResume(letRecursive.Body, handleResultType, out var letRecursiveBody, out error))
                {
                    return false;
                }

                rewritten = CopySpan(letRecursive, letRecursive with { Body = letRecursiveBody });
                return true;

            case Expr.If iff:
                if (ExprReferencesName(iff.Cond, "resume", shadowed: false))
                {
                    error = UnsupportedResumePosition;
                    return false;
                }

                if (!TryRewriteResume(iff.Then, handleResultType, out var thenBody, out error)
                    || !TryRewriteResume(iff.Else, handleResultType, out var elseBody, out error))
                {
                    return false;
                }

                rewritten = CopySpan(iff, new Expr.If(iff.Cond, thenBody, elseBody));
                return true;

            case Expr.Match { Value: Expr.Call { Func: Expr.Var { Name: "resume" } } scrutineeResume } oneShotMatch:
                if (ExprReferencesName(scrutineeResume.Arg, "resume", shadowed: false)
                    || oneShotMatch.Cases.Any(c => ExprReferencesName(c.Body, "resume", shadowed: false)
                        || (c.Guard is not null && ExprReferencesName(c.Guard, "resume", shadowed: false))))
                {
                    error = "'resume' may run at most once per path (multi-shot handlers are out of scope).";
                    return false;
                }

                var postParam = $"__resume_result_{_nextEffectSiteId++}";
                var postScrutinee = new Expr.Var(postParam);
                AstSpans.Set(postScrutinee, GetSpan(oneShotMatch));
                var postMatch = new Expr.Match(postScrutinee, oneShotMatch.Cases, oneShotMatch.Pos);
                AstSpans.Set(postMatch, AstSpans.GetOrDefault(oneShotMatch));
                rewritten = BuildEffectPost(postParam, postMatch, scrutineeResume.Arg, handleResultType, oneShotMatch);
                return true;

            case Expr.Match match:
                if (ExprReferencesName(match.Value, "resume", shadowed: false)
                    || match.Cases.Any(c => c.Guard is not null && ExprReferencesName(c.Guard, "resume", shadowed: false)))
                {
                    error = UnsupportedResumePosition;
                    return false;
                }

                var cases = new List<MatchCase>(match.Cases.Count);
                foreach (var matchCase in match.Cases)
                {
                    if (!TryRewriteResume(matchCase.Body, handleResultType, out var caseBody, out error))
                    {
                        return false;
                    }

                    cases.Add(new MatchCase(matchCase.Pattern, caseBody, matchCase.Guard));
                }

                rewritten = CopySpan(match, new Expr.Match(match.Value, cases, match.Pos));
                return true;

            default:
                error = ExprReferencesName(expr, "resume", shadowed: false)
                    ? UnsupportedResumePosition
                    : "every path of an operation arm must call resume(...) (aborting arms need unwinding and are not supported).";
                return false;
        }
    }

    /// <summary>
    /// Builds the one-shot split node: the resume argument as the value returned to the perform
    /// site, and <c>given param -&gt; postBody</c> as the post-resume continuation.
    /// </summary>
    private Expr BuildEffectPost(string paramName, Expr postBody, Expr resumeArg, TypeRef handleResultType, Expr original)
    {
        var postLambda = new Expr.Lambda(paramName, postBody);
        AstSpans.Set(postLambda, AstSpans.GetOrDefault(original));
        var node = new CapabilityPostExpr(resumeArg, postLambda, handleResultType);
        AstSpans.Set(node, AstSpans.GetOrDefault(original));
        return node;
    }

    private static Expr CopySpan(Expr original, Expr copy)
    {
        AstSpans.Set(copy, AstSpans.GetOrDefault(original));
        return copy;
    }

    /// <summary>
    /// Unifies a lowered arm closure against the operation's type. A rewritten tail-resumptive arm
    /// returns exactly what it resumes with, so the arm closure's shape is the operation's own
    /// function type. The arm's capability rows are detached (replaced by fresh variables) first: the
    /// arm's real row belongs to the enclosing context, not to the operation's published type.
    /// </summary>
    private void UnifyArmWithOperation(CapabilitySymbol capability, string opName, IReadOnlyList<TypeRef> effectArgs, TypeRef armClosureType, int armParamCount)
    {
        var operation = capability.Operations[opName];
        if (operation.DeclaredSignature is not null && CountArrows(operation.DeclaredSignature) < armParamCount)
        {
            ReportDiagnostic(0, $"Handler arm '{capability.Name}.{opName}' has {armParamCount} parameter(s) but the operation takes {CountArrows(operation.DeclaredSignature)}.", InvalidHandlerCode);
            return;
        }

        var opType = operation.DeclaredSignature is not null
            ? InstantiateEffectSignature(operation.DeclaredSignature, capability.TypeParameters, effectArgs)
            : operation.InferredType!;

        using (PushDiagnosticContext($"in handler arm '{capability.Name}.{opName}'"))
        {
            Unify(DetachRows(armClosureType), opType);
        }
    }

    private int CountArrows(TypeRef type)
    {
        int count = 0;
        var current = Prune(type);
        while (current is TypeRef.TFun funType)
        {
            count++;
            current = Prune(funType.Ret);
        }

        return count;
    }

    /// <summary>Copies a type with every arrow's row replaced by a fresh, unconstrained variable.</summary>
    private TypeRef DetachRows(TypeRef type)
    {
        type = Prune(type);
        return type switch
        {
            TypeRef.TFun funType => new TypeRef.TFun(DetachRows(funType.Arg), DetachRows(funType.Ret)) { Row = NewTypeVar() },
            TypeRef.TList list => new TypeRef.TList(DetachRows(list.Element)),
            TypeRef.TTuple tuple => new TypeRef.TTuple(tuple.Elements.Select(DetachRows).ToList()),
            TypeRef.TNamedType named => new TypeRef.TNamedType(named.Symbol, named.TypeArgs.Select(DetachRows).ToList()),
            _ => type,
        };
    }

    /// <summary>
    /// Eta-expands a first-class operation reference: <c>Clock.now</c> becomes
    /// <c>given a -&gt; Clock.now(a)</c> (curried to the signature's arity), so the perform happens
    /// at the eventual application site and normal closure lowering applies.
    /// </summary>
    private Expr BuildOperationEtaLambda(Expr.QualifiedVar qv, int arity)
    {
        var span = AstSpans.GetOrDefault(qv);
        var paramNames = Enumerable.Range(0, Math.Max(arity, 1))
            .Select(i => $"__op_arg_{_nextEffectSiteId++}_{i}")
            .ToList();

        Expr body = new Expr.QualifiedVar(qv.Module, qv.Name);
        AstSpans.Set(body, span);
        foreach (var paramName in paramNames)
        {
            var argVar = new Expr.Var(paramName);
            AstSpans.Set(argVar, span);
            body = new Expr.Call(body, argVar);
            AstSpans.Set(body, span);
        }

        for (int i = paramNames.Count - 1; i >= 0; i--)
        {
            body = new Expr.Lambda(paramNames[i], body);
            AstSpans.Set(body, span);
        }

        return body;
    }

    /// <summary>The row of the innermost arrow of a curried function type (where the capabilities fire).</summary>
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
    private TypeRef ResolveNeedsRow(NeedsRowSyntax row)
    {
        var capabilities = new List<TypeRef.TCapability>();
        foreach (var effectRef in row.Capabilities)
        {
            if (!_capabilitySymbols.TryGetValue(effectRef.Name, out var symbol))
            {
                ReportDiagnostic(0, $"Unknown capability '{effectRef.Name}' in needs row.", UnknownCapabilityCode);
                continue;
            }

            if (effectRef.Args.Count != symbol.TypeParameters.Count)
            {
                ReportDiagnostic(0, $"Capability '{effectRef.Name}' expects {symbol.TypeParameters.Count} type argument(s) but got {effectRef.Args.Count}.", UnknownCapabilityCode);
                continue;
            }

            capabilities.Add(new TypeRef.TCapability(symbol, effectRef.Args.Select(ResolveTypeExpr).ToList()));
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

        return new TypeRef.TRow(capabilities, tail);
    }

    // Named row variables of the annotation currently being resolved (shared by name within one
    // annotation, fresh across annotations), plus the type-parameter scope for capability operation
    // signatures (`capability State(a) = | get : Unit -> a`).
    private Dictionary<string, TypeRef>? _annotationRowVars;
    private Dictionary<string, TypeRef>? _typeExprParamScope;

    /// <summary>
    /// Resolves a user-written annotation with a fresh row-variable scope (and, for capability
    /// operation signatures, the capability's type parameters in scope).
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
