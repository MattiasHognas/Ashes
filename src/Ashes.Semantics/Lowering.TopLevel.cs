using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{

    // True when the body compares (`==`/`!=`) or adds (`+`) two of the function's own parameters
    // directly — the shape whose operand type is a generalizable variable that `==`/`+` cannot pick a
    // single IR op for. Such functions are inlined per concrete call site (see _overloadGenericInline).
    private static bool BodyComparesOrAddsParameters(Expr expr, IReadOnlyList<string> paramNames)
    {
        var parameters = new HashSet<string>(paramNames, StringComparer.Ordinal);
        var found = false;

        bool IsParam(Expr e) => e is Expr.Var v && parameters.Contains(v.Name);

        void Visit(object? node)
        {
            if (found || node is null or string)
            {
                return;
            }

            if ((node is Expr.Equal eq && IsParam(eq.Left) && IsParam(eq.Right))
                || (node is Expr.NotEqual ne && IsParam(ne.Left) && IsParam(ne.Right))
                || (node is Expr.Add add && IsParam(add.Left) && IsParam(add.Right)))
            {
                found = true;
                return;
            }

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

    // Maps the unqualified export name of a stitched overload-generic stdlib function to its canonical
    // stitched name (Ashes_Test_assertEqual → alias "assertEqual"), so a call by the imported short
    // name still finds the registration. Collisions are recorded as null (ambiguous → not inlined).
    private void RegisterOverloadGenericAlias(string canonicalName)
    {
        foreach (var moduleName in BuiltinRegistry.StandardModuleNames)
        {
            string prefix = ProjectSupport.SanitizeModuleBindingName(moduleName) + "_";
            if (canonicalName.StartsWith(prefix, StringComparison.Ordinal))
            {
                string shortName = canonicalName.Substring(prefix.Length);
                if (shortName.Length == 0)
                {
                    continue;
                }

                // Ambiguous if another module already claimed this short name.
                _overloadGenericAlias[shortName] = _overloadGenericAlias.ContainsKey(shortName) ? null : canonicalName;
            }
        }
    }

    private void RegisterInlinableFunctions(IReadOnlyList<TopLevelItem> valueItems)
    {
        // Strip the import stitcher's intra-module alias prefix (let helper = Ashes_Mod_helper in ...)
        // so a stdlib function's value is seen as the clean lambda it is — otherwise the reuse
        // registries below never recognise stdlib functions (Map.set etc.), since their value is a
        // chain of alias `let`s rather than a Lambda. On an unhandled shape, fall back to the raw value
        // (the function just isn't registered for reuse — correct, only unoptimized).
        Expr Strip(Expr value)
        {
            try
            {
                return StripModuleAliasPrefix(value);
            }
            catch (NotSupportedException)
            {
                return value;
            }
        }

        foreach (var item in valueItems)
        {
            // The grained data-parallel combinators: capture their stripped multi-parameter recursive
            // lambda so each concrete-typed call site can generate a monomorphic parallel specialization.
            // mapGrained is grain+f+xs (arity 3); reduceGrained is grain+combine+identity+f+xs (arity 5).
            if (item is TopLevelItem.LetDecl { IsRecursive: true } parLet
                && (string.Equals(parLet.Name, ParallelMapGrainedName, StringComparison.Ordinal)
                    || string.Equals(parLet.Name, ParallelReduceGrainedName, StringComparison.Ordinal))
                && Strip(parLet.Value) is Expr.Lambda parLam)
            {
                int arity = string.Equals(parLet.Name, ParallelMapGrainedName, StringComparison.Ordinal) ? 3 : 5;
                _parallelSpecializable[parLet.Name] = (parLam, arity);
            }

            if (item is TopLevelItem.LetDecl { IsRecursive: false } let && Strip(let.Value) is Expr.Lambda lam)
            {
                // A non-recursive function that returns a nested recursive single-param function
                // (the Map.set shape) is specialized, not inlined; any other plain function is an
                // inline candidate for helper rebuilds inside a reuse arm.
                if (TryGetNestedRecursiveReturn(lam, out var nestedParam, out var nestedArgCount))
                {
                    _specializableFunctions[let.Name] = (lam, nestedParam, nestedArgCount);
                }
                else if (ExprHasCallOrAggregate(GetInnermostBody(lam)))
                {
                    // Only inline helpers that can contribute an allocation. Non-allocating accessor /
                    // arithmetic helpers are resolved as by-label calls in specializations instead (see
                    // _topLevelFunctionRefs), which keeps the specialized function small.
                    _inlinableFunctions[let.Name] = (CollectLambdaParams(lam), GetInnermostBody(lam));
                    _inlinableDefiningValues[let.Name] = let.Value;
                }

                // Capability-generic: inline at concrete call sites so a parameterized capability
                // operation resolves to its provider. Registered independently of the reuse-inline
                // filter above (which requires an allocation).
                if (BodyPerformsProvidedParameterizedCapability(GetInnermostBody(lam)))
                {
                    _inlinableFunctions.TryAdd(let.Name, (CollectLambdaParams(lam), GetInnermostBody(lam)));
                    _inlinableDefiningValues.TryAdd(let.Name, let.Value);
                    _capabilityGenericInline.Add(let.Name);
                }

                // Overload-generic: compares/adds two parameters, so it can be used at Int/Str/Bool/
                // Float across one program by inlining a type-resolved copy per concrete call site.
                if (BodyComparesOrAddsParameters(GetInnermostBody(lam), CollectLambdaParams(lam)))
                {
                    _inlinableFunctions.TryAdd(let.Name, (CollectLambdaParams(lam), GetInnermostBody(lam)));
                    _inlinableDefiningValues.TryAdd(let.Name, let.Value);
                    _overloadGenericInline.Add(let.Name);
                    RegisterOverloadGenericAlias(let.Name);
                }
            }

            // Single-parameter recursive functions (let rec f = given p -> body, body not a lambda)
            // are candidates for in-place-reuse specialization when applied to a unique accumulator.
            else if (item is TopLevelItem.LetDecl { IsRecursive: true } recursiveLet && Strip(recursiveLet.Value) is Expr.Lambda { Body: not Expr.Lambda } recursiveLambda)
            {
                _specializableFunctions[recursiveLet.Name] = (recursiveLambda, recursiveLambda.ParamName, 1);
            }
            else if (item is TopLevelItem.RecursiveGroup { Bindings.Count: 1 } group
                && Strip(group.Bindings[0].Value) is Expr.Lambda { Body: not Expr.Lambda } groupLam)
            {
                _specializableFunctions[group.Bindings[0].Name] = (groupLam, groupLam.ParamName, 1);
            }
        }
    }

    /// <summary>
    /// Registers functions bound in the entry expression's leading let-chain for the same reuse
    /// registries as flat top-level items. When the import stitcher combines modules with the user
    /// program, the user's top-level declarations arrive as a nested let-pyramid in the program
    /// BODY (not as TopLevelItems), so without this walk user-defined functions could never be
    /// reuse-specialized or helper-inlined — only stitched module (Ashes_*) functions could.
    /// Shadow safety: the registries are name-keyed, so a name bound more than once anywhere in
    /// the body (any let/lambda/pattern binder) is never registered.
    /// </summary>
    private void RegisterEntryBodyFunctions(Expr body)
    {
        var binderCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        CountBinders(body, binderCounts);

        // Collect the entry let-chain's function candidates: single-binder (unshadowed) lambda
        // bindings not already registered from a flat top-level item.
        var candidates = new List<(string Name, bool IsRecursive, Expr.Lambda Lam)>();
        var candidateNames = new HashSet<string>(StringComparer.Ordinal);
        var cur = body;
        while (cur is Expr.Let or Expr.LetRecursive)
        {
            (string name, bool isRecursive, Expr value, Expr next) = cur switch
            {
                Expr.Let l => (l.Name, false, l.Value, l.Body),
                Expr.LetRecursive lr => (lr.Name, true, lr.Value, lr.Body),
                _ => throw new InvalidOperationException(),
            };

            if (binderCounts.GetValueOrDefault(name) == 1
                && !_specializableFunctions.ContainsKey(name)
                && !_inlinableFunctions.ContainsKey(name)
                && value is Expr.Lambda lam)
            {
                candidates.Add((name, isRecursive, lam));
                candidateNames.Add(name);
            }

            cur = next;
        }

        if (candidates.Count == 0)
        {
            return;
        }

        // A candidate is REGISTERABLE iff every free variable of its body resolves at an inline or
        // specialization site — i.e. is a stitched module binding, a constructor, an
        // already-registered function, or another registerable candidate. (A registerable function
        // therefore captures nothing that isn't globally resolvable, so it reaches _topLevelFunctionRefs
        // as an empty-env by-label callee.) Referencing any OTHER user sibling — a captured value, a
        // non-registerable helper — would fail to lower inside a spec (the earlier bug this gate guards),
        // so drop it. Compute the maximal registerable set by removing violators to a fixpoint.
        var freeVarsByName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (name, _, lam) in candidates)
        {
            freeVarsByName[name] = FreeVars(lam, new HashSet<string>(StringComparer.Ordinal) { name });
        }

        bool GloballyResolvable(string n) =>
            _topLevelBindingNames.Contains(n)
            || _constructorSymbols.ContainsKey(n)
            || _specializableFunctions.ContainsKey(n)
            || _inlinableFunctions.ContainsKey(n);

        var registerable = new HashSet<string>(candidateNames, StringComparer.Ordinal);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var name in candidateNames)
            {
                if (!registerable.Contains(name))
                {
                    continue;
                }

                if (freeVarsByName[name].Any(fv => !GloballyResolvable(fv) && !registerable.Contains(fv)))
                {
                    registerable.Remove(name);
                    changed = true;
                }
            }
        }

        // Register each registerable candidate, in let-chain order, exactly like a flat top-level item:
        // the nested-recursive-return / single-param-recursive shapes become reuse specializations, and
        // any other non-recursive helper with an allocating body becomes an inlinable rebuild helper.
        foreach (var (name, isRecursive, lam) in candidates)
        {
            if (!registerable.Contains(name))
            {
                continue;
            }

            if (!isRecursive)
            {
                if (TryGetNestedRecursiveReturn(lam, out var nestedParam, out var nestedArgCount))
                {
                    _specializableFunctions[name] = (lam, nestedParam, nestedArgCount);
                }
                else if (ExprHasCallOrAggregate(GetInnermostBody(lam)))
                {
                    _inlinableFunctions[name] = (CollectLambdaParams(lam), GetInnermostBody(lam));
                    _inlinableDefiningValues[name] = lam;
                }

                // Capability monomorphization (see RegisterInlinableFunctions): inline at concrete
                // call sites so a parameterized capability operation resolves to its provider.
                if (BodyPerformsProvidedParameterizedCapability(GetInnermostBody(lam)))
                {
                    _inlinableFunctions.TryAdd(name, (CollectLambdaParams(lam), GetInnermostBody(lam)));
                    _inlinableDefiningValues.TryAdd(name, lam);
                    _capabilityGenericInline.Add(name);
                }

                // Overload-generic (see RegisterInlinableFunctions): a user helper that compares/adds
                // two of its parameters is inlined per concrete call site so it works across types.
                if (BodyComparesOrAddsParameters(GetInnermostBody(lam), CollectLambdaParams(lam)))
                {
                    _inlinableFunctions.TryAdd(name, (CollectLambdaParams(lam), GetInnermostBody(lam)));
                    _inlinableDefiningValues.TryAdd(name, lam);
                    _overloadGenericInline.Add(name);
                }
            }
            else if (lam.Body is not Expr.Lambda)
            {
                // Single-parameter recursive function — a direct reuse-specialization candidate.
                _specializableFunctions[name] = (lam, lam.ParamName, 1);
            }
        }
    }

    /// <summary>
    /// Counts every binder occurrence (let / let rec / lambda parameter / match-pattern variable)
    /// by name across an expression tree. Walks record properties reflectively so new Expr shapes
    /// are covered by default.
    /// </summary>
    private static void CountBinders(object? node, Dictionary<string, int> counts)
    {
        if (node is null or string)
        {
            return;
        }

        void Bump(string n) => counts[n] = counts.GetValueOrDefault(n) + 1;
        switch (node)
        {
            case Expr.Let l: Bump(l.Name); break;
            case Expr.LetResult lr: Bump(lr.Name); break;
            case Expr.LetRecursive lrec: Bump(lrec.Name); break;
            case Expr.Lambda lam: Bump(lam.ParamName); break;
            case Pattern.Var pv: Bump(pv.Name); break;
        }

        if (node is System.Runtime.CompilerServices.ITuple tuple)
        {
            for (int i = 0; i < tuple.Length; i++)
            {
                CountBinders(tuple[i], counts);
            }

            return;
        }

        if (node is System.Collections.IEnumerable seq)
        {
            foreach (var item in seq)
            {
                CountBinders(item, counts);
            }

            return;
        }

        if (node is not (Expr or Pattern or MatchCase or HandlerArm))
        {
            return;
        }

        foreach (var prop in node.GetType().GetProperties())
        {
            if (prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var t = prop.PropertyType;
            if (typeof(Expr).IsAssignableFrom(t)
                || typeof(Pattern).IsAssignableFrom(t)
                || typeof(MatchCase).IsAssignableFrom(t)
                || (typeof(System.Collections.IEnumerable).IsAssignableFrom(t) && t != typeof(string)))
            {
                CountBinders(prop.GetValue(node), counts);
            }
        }
    }

    /// <summary>
    /// Detects the nested-recursive-return shape and the parameter to specialize on. Two forms:
    /// <list type="bullet">
    /// <item><b>Bare</b> (<c>Map.set</c>): a chain of outer parameter lambdas whose innermost body is
    /// <c>let rec go = (given m -> _) in go</c> — the recursive worker returned bare. The caller
    /// applies the accumulator to the returned worker, so <c>argCount = outerParams + 1</c>.</item>
    /// <item><b>Eta-applied</b> (<c>HashMap.set</c>): the worker is instead returned as
    /// <c>… in go(map)</c>, where <c>map</c> is the last outer parameter. This just forwards a fresh
    /// outer accumulator straight into the worker, so the worker's own parameter is still the linear
    /// reuse root, but the accumulator is already an outer argument, so <c>argCount = outerParams</c>.</item>
    /// </list>
    /// In both forms a chain of leading non-recursive <c>let</c> bindings before the <c>let rec</c>
    /// (e.g. <c>let target = hashKey(newKey)</c>) is peeled: those lower once in the outer function
    /// before the worker is created and do not affect the accumulator's linearity. Outputs the
    /// worker's parameter to specialize on and the total number of arguments the full application takes.
    /// </summary>
    private static bool TryGetNestedRecursiveReturn(Expr.Lambda lam, out string linearParam, out int argCount)
    {
        linearParam = "";
        argCount = 0;
        Expr body = lam;
        var outerParams = new List<string>();
        while (body is Expr.Lambda inner)
        {
            outerParams.Add(inner.ParamName);
            body = inner.Body;
        }

        // Peel leading non-recursive lets (hoisted per-call setup like the composite hash key).
        while (body is Expr.Let { Body: var letBody })
        {
            body = letBody;
        }

        if (body is not Expr.LetRecursive { Value: Expr.Lambda recursiveValue } letRecursive
            || recursiveValue.Body is Expr.Lambda)
        {
            return false;
        }

        // Bare return `in go`: the caller supplies the accumulator as one extra argument.
        if (letRecursive.Body is Expr.Var recursiveRef
            && string.Equals(letRecursive.Name, recursiveRef.Name, StringComparison.Ordinal))
        {
            linearParam = recursiveValue.ParamName;
            argCount = outerParams.Count + 1;
            return true;
        }

        // Eta return `in go(lastOuterParam)`: the accumulator is already the last outer argument,
        // forwarded verbatim into the worker, whose own parameter stays the linear reuse root.
        if (letRecursive.Body is Expr.Call { Func: Expr.Var etaFunc, Arg: Expr.Var etaArg }
            && string.Equals(etaFunc.Name, letRecursive.Name, StringComparison.Ordinal)
            && outerParams.Count > 0
            && string.Equals(etaArg.Name, outerParams[^1], StringComparison.Ordinal))
        {
            linearParam = recursiveValue.ParamName;
            argCount = outerParams.Count;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Records every top-level value-binding name and reports duplicates (ASH013). The recorded set
    /// later lets <see cref="LowerVar"/> distinguish a forward reference from an unknown identifier.
    /// </summary>
    private void CollectTopLevelBindingNames(IReadOnlyList<TopLevelItem> valueItems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in valueItems)
        {
            switch (item)
            {
                case TopLevelItem.LetDecl let:
                    RegisterTopLevelBindingName(let.Name, let.Value, seen);
                    break;
                case TopLevelItem.RecursiveGroup group:
                    foreach (var (name, value) in group.Bindings)
                    {
                        RegisterTopLevelBindingName(name, value, seen);
                    }

                    break;
            }
        }
    }

    private void RegisterTopLevelBindingName(string name, Expr valueForSpan, HashSet<string> seen)
    {
        _topLevelBindingNames.Add(name);
        if (!seen.Add(name))
        {
            ReportDiagnostic(GetSpan(valueForSpan), $"Duplicate top-level binding '{name}'.", DuplicateTopLevelBindingCode);
        }
    }

    private Expr DesugarTopLevel(IReadOnlyList<TopLevelItem> valueItems, Expr? trailingBody)
    {
        // A program may omit the trailing expression (e.g. a module that only declares bindings);
        // synthesize a unit value so the entry point is well-typed and produces no output.
        Expr body = trailingBody ?? new Expr.Var("Unit");

        for (int i = valueItems.Count - 1; i >= 0; i--)
        {
            body = valueItems[i] switch
            {
                TopLevelItem.LetDecl { IsRecursive: true } let => new Expr.LetRecursive(let.Name, let.Value, body) { TypeAnnotation = let.TypeAnnotation },
                TopLevelItem.LetDecl let => new Expr.Let(let.Name, let.Value, body) { TypeAnnotation = let.TypeAnnotation },
                TopLevelItem.RecursiveGroup group => DesugarRecursiveGroup(group, body),
                _ => body
            };
        }

        return body;
    }

    /// <summary>
    /// Desugars a mutual-recursion group (<c>let rec X = ... and Y = ...</c>) into a marker node that
    /// lowering handles directly. The parser only emits a <see cref="TopLevelItem.RecursiveGroup"/> for a
    /// genuine multi-binding <c>and</c> group (or a degenerate one-binding group), whose bindings must
    /// all see one another — a property that nesting independent <c>let rec</c> forms cannot express.
    /// The group's names are also in scope for the continuation (subsequent declarations and the
    /// trailing expression) under Model-A scoping; <paramref name="body"/> carries that continuation.
    /// </summary>
    private Expr DesugarRecursiveGroup(TopLevelItem.RecursiveGroup group, Expr body)
        => new RecursiveGroupExpr(group.Bindings, body);

    /// <summary>
    /// Internal-only AST node carrying a mutual-recursion binding group plus its continuation. It only
    /// ever appears at the top level (never inside a lambda/async body), so the free-variable and
    /// other expression walkers never encounter it; only <see cref="LowerExpr"/> dispatches it.
    /// </summary>
    private sealed record RecursiveGroupExpr(IReadOnlyList<(string Name, Expr Value)> Bindings, Expr Body) : Expr;

    /// <summary>
    /// Shared lowering state for the members of a mutual-recursion group. Every member is compiled to
    /// its own IR function but they all share one identical environment, so a member can rebuild any
    /// sibling's closure from its own env pointer (see <see cref="LowerLambdaCore"/>).
    /// </summary>
    private sealed class RecursiveGroupContext
    {
        public required IReadOnlyList<string> SharedCaptures { get; init; }
        public required int SharedEnvPtrTemp { get; init; }
        public required IReadOnlyList<(string Name, string Label, TypeRef Type, TextSpan Span)> Members { get; init; }
    }

    /// <summary>
    /// Lowers a mutually-recursive binding group. All member names are introduced with fresh type
    /// variables before any right-hand side is inferred (so each body sees every member), every body is
    /// lowered against one shared environment, and the names then stay in scope — monomorphically, as
    /// the single <c>let rec</c> form already does — for the continuation.
    /// </summary>
    private (int, TypeRef) LowerRecursiveGroup(RecursiveGroupExpr group)
    {
        var bindings = group.Bindings;
        var groupNames = new HashSet<string>(bindings.Select(b => b.Name), StringComparer.Ordinal);

        // HM recursive-group rule, part 1: introduce a fresh type for every member up front. Function
        // members get an arrow shape so call sites refine arg/return before the body is solved.
        var recordTypes = new TypeRef[bindings.Count];
        for (int i = 0; i < bindings.Count; i++)
        {
            recordTypes[i] = FindInnermostLambdaUnderLets(bindings[i].Value) is not null
                ? new TypeRef.TFun(NewTypeVar(), NewTypeVar())
                : NewTypeVar();
        }

        // The shared environment is the union of each member body's free variables, minus the group
        // names themselves (siblings are reached by symbol, never captured). Order is fixed here and
        // reused for every member so the env layout is identical across the group.
        var sharedCaptures = new List<string>();
        var seenCaptures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, value) in bindings)
        {
            foreach (var name in FreeVars(value, groupNames))
            {
                if (seenCaptures.Add(name)
                    && Lookup(name) is Binding.Local or Binding.Env or Binding.EnvScheme or Binding.Self or Binding.Scheme)
                {
                    sharedCaptures.Add(name);
                }
            }
        }

        // Build the shared env once, at the group site, where the captured names are still in scope.
        int sharedEnvPtrTemp = NewTemp();
        if (sharedCaptures.Count == 0)
        {
            Emit(new IrInst.LoadConstInt(sharedEnvPtrTemp, 0)); // null env
        }
        else
        {
            Emit(new IrInst.Alloc(sharedEnvPtrTemp, sharedCaptures.Count * 8));
            for (int i = 0; i < sharedCaptures.Count; i++)
            {
                var (capTemp, _) = LowerVar(new Expr.Var(sharedCaptures[i]));
                Emit(new IrInst.StoreMemOffset(sharedEnvPtrTemp, i * 8, capTemp));
            }
        }

        var labels = new string[bindings.Count];
        var members = new (string Name, string Label, TypeRef Type, TextSpan Span)[bindings.Count];
        var slots = new int[bindings.Count];
        for (int i = 0; i < bindings.Count; i++)
        {
            labels[i] = $"recgroup_{_nextLambdaId++}_{bindings[i].Name}";
            members[i] = (bindings[i].Name, labels[i], recordTypes[i], GetSpan(bindings[i].Value));
            slots[i] = NewLocal();
        }

        var groupContext = new RecursiveGroupContext
        {
            SharedCaptures = sharedCaptures,
            SharedEnvPtrTemp = sharedEnvPtrTemp,
            Members = members
        };

        // Mutual recursion is reached through closure calls, not the single-function tail-call loop, so
        // disable TCO while lowering the group bodies.
        var savedTcoCtx = _tcoCtx;
        _tcoCtx = null;

        for (int i = 0; i < bindings.Count; i++)
        {
            var value = bindings[i].Value;
            if (value is not Expr.Lambda lambda)
            {
                ReportDiagnostic(GetSpan(value), "let recursive currently requires a function value.");
                var (fallbackTemp, fallbackType) = LowerExpr(value);
                Unify(recordTypes[i], fallbackType);
                Emit(new IrInst.StoreLocal(slots[i], fallbackTemp));
                RecordLocalDebugInfo(slots[i], bindings[i].Name, recordTypes[i]);
                continue;
            }

            var (closureTemp, closureType) = LowerLambdaCore(lambda, selfName: null, selfType: null, stackAllocateClosure: false, recursiveGroup: groupContext, forcedLabel: labels[i]);
            Unify(recordTypes[i], closureType);
            RecordHoverType(members[i].Span, bindings[i].Name, recordTypes[i]);
            RecordLocalDebugInfo(slots[i], bindings[i].Name, recordTypes[i]);
            Emit(new IrInst.StoreLocal(slots[i], closureTemp));
        }

        _tcoCtx = savedTcoCtx;

        // Mutual-recursion TCO: when the group is eligible, synthesize a single self-recursive
        // dispatch function and rebind each member to a thin wrapper so the existing single-function
        // TCO collapses the whole group into one loop instead of growing the stack through closure
        // calls. Ineligible groups keep the closures lowered above.
        var tcoSlots = TryLowerMutualRecursionTco(bindings, recordTypes, groupNames);

        // The members stay in scope for the continuation, bound to the slots holding their closures
        // (or their TCO wrappers) — monomorphic, matching the single let rec form.
        var parent = _scopes.Peek();
        var child = new Dictionary<string, Binding>(parent, StringComparer.Ordinal);
        for (int i = 0; i < bindings.Count; i++)
        {
            int memberSlot = tcoSlots?[i] ?? slots[i];
            child[bindings[i].Name] = new Binding.Local(memberSlot, Prune(recordTypes[i]), members[i].Span);
        }

        _scopes.Push(child);
        var (bodyTemp, bodyType) = LowerExpr(group.Body);
        _scopes.Pop();
        return (bodyTemp, bodyType);
    }

    private static string DispatchArgName(int index) => $"__recgroup_arg{index}";

    private static string WrapperArgName(int index) => $"__recgroup_w{index}";

    /// <summary>
    /// Attempts to compile a mutually-recursive group as a single self-recursive dispatch loop so
    /// mutual tail calls stop growing the stack. Returns one wrapper slot per member when the
    /// transform was applied, or <c>null</c> to keep the closure-based lowering produced above.
    /// <para>
    /// Eligible groups have at least two members, all function values of the same arity with
    /// identical parameter types (checked against each member's inferred signature), and at least
    /// one cross-member tail call. Identical parameter types mean every merged dispatch parameter
    /// keeps a single well-defined type, so the existing single-function TCO — including its arena
    /// copy-out — applies unchanged. Heterogeneous parameter types fall back to the closure path.
    /// </para>
    /// </summary>
    private int[]? TryLowerMutualRecursionTco(
        IReadOnlyList<(string Name, Expr Value)> bindings,
        TypeRef[] recordTypes,
        HashSet<string> groupNames)
    {
        if (bindings.Count < 2)
        {
            return null;
        }

        // All members must be lambdas of the same arity.
        var lambdas = new Expr.Lambda[bindings.Count];
        int arity = -1;
        for (int i = 0; i < bindings.Count; i++)
        {
            if (bindings[i].Value is not Expr.Lambda lam)
            {
                return null;
            }

            lambdas[i] = lam;
            int memberArity = CountLambdaChain(lam);
            if (arity == -1)
            {
                arity = memberArity;
            }
            else if (memberArity != arity)
            {
                return null;
            }
        }

        if (arity < 1)
        {
            return null;
        }

        // Every member must expose the same parameter type at each position so the shared dispatch
        // parameters are well-typed without coercion.
        var paramTypes = new List<TypeRef>[bindings.Count];
        for (int i = 0; i < bindings.Count; i++)
        {
            if (!TryGetArrowParamTypes(recordTypes[i], arity, out var memberParamTypes))
            {
                return null;
            }

            paramTypes[i] = memberParamTypes;
        }

        for (int j = 0; j < arity; j++)
        {
            for (int i = 1; i < bindings.Count; i++)
            {
                if (!TypesStructurallyEqual(paramTypes[0][j], paramTypes[i][j]))
                {
                    return null;
                }
            }
        }

        var tagOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < bindings.Count; i++)
        {
            tagOf[bindings[i].Name] = i;
        }

        // At least one genuine cross-member tail call — otherwise single-function TCO already covers it.
        bool hasCrossMemberTailCall = false;
        for (int i = 0; i < bindings.Count && !hasCrossMemberTailCall; i++)
        {
            var called = new HashSet<string>(StringComparer.Ordinal);
            CollectGroupTailCalls(GetInnermostBody(lambdas[i]), groupNames, arity, called);
            hasCrossMemberTailCall = called.Any(name => !string.Equals(name, bindings[i].Name, StringComparison.Ordinal));
        }

        if (!hasCrossMemberTailCall)
        {
            return null;
        }

        // ── Synthesize and lower the dispatch function. ──
        string dispatchName = $"__recgroup_dispatch_{_nextLambdaId++}";
        var dispatchLambda = BuildDispatchLambda(bindings, lambdas, groupNames, tagOf, dispatchName, arity);

        int dispatchSlot = NewLocal();
        var dispatchRecursiveType = (TypeRef)new TypeRef.TFun(NewTypeVar(), NewTypeVar());
        var dispatchScope = new Dictionary<string, Binding>(_scopes.Peek(), StringComparer.Ordinal)
        {
            [dispatchName] = new Binding.Local(dispatchSlot, dispatchRecursiveType)
        };
        _scopes.Push(dispatchScope);

        int paramCount = arity + 1; // the dispatch tag plus the shared parameters
        var savedTcoCtx = _tcoCtx;
        _tcoCtx = HasTailSelfCalls(GetInnermostBody(dispatchLambda), dispatchName, paramCount)
            ? new TcoContext
            {
                SelfName = dispatchName,
                ParamCount = paramCount,
                ParamNames = CollectLambdaParams(dispatchLambda),
                InTailPosition = false
            }
            : null;
        var (dispatchTemp, dispatchType) = LowerLambdaCore(
            dispatchLambda, dispatchName, dispatchRecursiveType, stackAllocateClosure: false, forcedLabel: dispatchName);
        _tcoCtx = savedTcoCtx;
        Unify(dispatchRecursiveType, dispatchType);
        Emit(new IrInst.StoreLocal(dispatchSlot, dispatchTemp));

        // ── Synthesize and lower one wrapper per member: given p… -> dispatch(tag, p…). ──
        var wrapperSlots = new int[bindings.Count];
        for (int i = 0; i < bindings.Count; i++)
        {
            var wrapperLambda = BuildDispatchWrapper(dispatchName, tagOf[bindings[i].Name], arity);
            var (wrapperTemp, wrapperType) = LowerExpr(wrapperLambda);
            Unify(recordTypes[i], wrapperType);
            int slot = NewLocal();
            Emit(new IrInst.StoreLocal(slot, wrapperTemp));
            RecordLocalDebugInfo(slot, bindings[i].Name, recordTypes[i]);
            wrapperSlots[i] = slot;
        }

        _scopes.Pop(); // dispatchScope — dispatchName must not leak into the continuation.
        return wrapperSlots;
    }

    /// <summary>
    /// Builds <c>given which -> given arg0 -> … -> match which with | 0 -> body0 | … | _ -> bodyN</c>,
    /// where each arm is a member body with its parameters rebound to the shared dispatch arguments
    /// and its in-group tail calls redirected to <paramref name="dispatchName"/>.
    /// </summary>
    private Expr.Lambda BuildDispatchLambda(
        IReadOnlyList<(string Name, Expr Value)> bindings,
        Expr.Lambda[] lambdas,
        HashSet<string> groupNames,
        Dictionary<string, int> tagOf,
        string dispatchName,
        int arity)
    {
        var cases = new List<MatchCase>(bindings.Count);
        for (int i = 0; i < bindings.Count; i++)
        {
            var origParams = CollectLambdaParams(lambdas[i]);
            var inner = GetInnermostBody(lambdas[i]);
            Expr armBody = RewriteGroupTailCalls(inner, groupNames, tagOf, dispatchName, arity);
            for (int j = arity - 1; j >= 0; j--)
            {
                armBody = new Expr.Let(origParams[j], new Expr.Var(DispatchArgName(j)), armBody);
            }

            // The final member is the wildcard arm so the integer dispatch match is exhaustive.
            Pattern pattern = i == bindings.Count - 1 ? new Pattern.Wildcard() : new Pattern.IntLit(i);
            cases.Add(new MatchCase(pattern, armBody));
        }

        Expr body = new Expr.Match(new Expr.Var(DispatchWhichName), cases);
        for (int j = arity - 1; j >= 0; j--)
        {
            body = new Expr.Lambda(DispatchArgName(j), body);
        }

        return new Expr.Lambda(DispatchWhichName, body);
    }

    /// <summary>Builds <c>given w0 -> … -> dispatch(tag, w0, …)</c>, the per-member entry wrapper.</summary>
    private Expr.Lambda BuildDispatchWrapper(string dispatchName, int tag, int arity)
    {
        Expr body = new Expr.Call(new Expr.Var(dispatchName), new Expr.IntLit(tag));
        for (int j = 0; j < arity; j++)
        {
            body = new Expr.Call(body, new Expr.Var(WrapperArgName(j)));
        }

        for (int j = arity - 1; j >= 0; j--)
        {
            body = new Expr.Lambda(WrapperArgName(j), body);
        }

        return (Expr.Lambda)body;
    }

    /// <summary>
    /// Rewrites fully-applied in-group member calls that sit in tail position into calls to the
    /// dispatch function, leaving non-tail occurrences (which still target the member wrappers)
    /// untouched. Only tail-position nodes are traversed.
    /// </summary>
    private Expr RewriteGroupTailCalls(Expr expr, HashSet<string> groupNames, Dictionary<string, int> tagOf, string dispatchName, int arity)
    {
        switch (expr)
        {
            case Expr.If iff:
                return iff with
                {
                    Then = RewriteGroupTailCalls(iff.Then, groupNames, tagOf, dispatchName, arity),
                    Else = RewriteGroupTailCalls(iff.Else, groupNames, tagOf, dispatchName, arity),
                };
            case Expr.Match m:
                return m with
                {
                    Cases = m.Cases
                        .Select(c => c with { Body = RewriteGroupTailCalls(c.Body, groupNames, tagOf, dispatchName, arity) })
                        .ToList(),
                };
            case Expr.Let l:
                return l with { Body = RewriteGroupTailCalls(l.Body, groupNames, tagOf, dispatchName, arity) };
            case Expr.LetRecursive l:
                return l with { Body = RewriteGroupTailCalls(l.Body, groupNames, tagOf, dispatchName, arity) };
            case Expr.LetResult l:
                return l with { Body = RewriteGroupTailCalls(l.Body, groupNames, tagOf, dispatchName, arity) };
            case Expr.Call call:
                var args = new List<Expr>();
                var root = CollectCallArgs(call, args);
                if (root is Expr.Var v && groupNames.Contains(v.Name) && args.Count == arity)
                {
                    Expr redirected = new Expr.Call(new Expr.Var(dispatchName), new Expr.IntLit(tagOf[v.Name]));
                    foreach (var arg in args)
                    {
                        redirected = new Expr.Call(redirected, arg);
                    }

                    return redirected;
                }

                return expr;
            default:
                return expr;
        }
    }

    /// <summary>Collects the names of group members tail-called (fully applied) within an expression.</summary>
    private void CollectGroupTailCalls(Expr expr, HashSet<string> groupNames, int arity, HashSet<string> found)
    {
        switch (expr)
        {
            case Expr.If iff:
                CollectGroupTailCalls(iff.Then, groupNames, arity, found);
                CollectGroupTailCalls(iff.Else, groupNames, arity, found);
                break;
            case Expr.Match m:
                foreach (var c in m.Cases)
                {
                    CollectGroupTailCalls(c.Body, groupNames, arity, found);
                }

                break;
            case Expr.Let l:
                CollectGroupTailCalls(l.Body, groupNames, arity, found);
                break;
            case Expr.LetRecursive l:
                CollectGroupTailCalls(l.Body, groupNames, arity, found);
                break;
            case Expr.LetResult l:
                CollectGroupTailCalls(l.Body, groupNames, arity, found);
                break;
            case Expr.Call call:
                var args = new List<Expr>();
                var root = CollectCallArgs(call, args);
                if (root is Expr.Var v && groupNames.Contains(v.Name) && args.Count == arity)
                {
                    found.Add(v.Name);
                }

                break;
        }
    }
}
