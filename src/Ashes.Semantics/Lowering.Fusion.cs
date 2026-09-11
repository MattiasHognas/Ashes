using System.Diagnostics.CodeAnalysis;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // Bounds the alias chase in ResolveCalleeQualifiedName: a pathologically long chain of
    // let-bound aliases-of-aliases gives up and declines rather than looping or recursing deeply.
    private const int CalleeIdentityChaseDepthLimit = 16;

    /// <summary>
    /// Resolves an arbitrary call root to the qualified source name of the declaration it names —
    /// a stdlib module member (<c>"Ashes.Collection.List.reverse"</c>) or an entry-module top-level
    /// binding — independent of whatever alias the call site actually spells. A qualified reference
    /// resolves algorithmically through <see cref="ResolveModuleAlias"/> (any module alias); a bare
    /// name resolves directly when it already IS the canonical stitched compiler name (the shape an
    /// unaliased qualified reference, or a reference from inside another stitched module's own body,
    /// takes), and otherwise chases through <see cref="_letBindingValues"/> — the import stitcher's
    /// own <c>let name = target in ...</c> aliases, and any alias the user's own source wrote, look
    /// identical here, both are just a let binding whose recorded value is resolved in turn. A
    /// shadowing binding is not a hazard: <see cref="Lookup"/> only ever returns what is actually in
    /// lexical scope at this call site, so a user's own same-named binding resolves through ITS OWN
    /// value, never the stdlib declaration it happens to share a spelling with.
    /// </summary>
    private string? ResolveCalleeQualifiedName(Expr root, int depth = 0)
    {
        if (depth > CalleeIdentityChaseDepthLimit)
        {
            return null;
        }

        switch (root)
        {
            case Expr.QualifiedVar qv:
                {
                    string resolvedModule = ResolveModuleAlias(qv.Module);
                    if (BuiltinRegistry.TryGetModule(resolvedModule, out var module)
                        && module.Members.ContainsKey(qv.Name))
                    {
                        // A compiler intrinsic, not a stitched .ash declaration — it has no
                        // qualified source name to compare against.
                        return null;
                    }

                    string compilerName = ProjectSupport.SanitizeModuleBindingName(resolvedModule) + "_" + qv.Name;
                    return _functionSourceNames is not null
                        && _functionSourceNames.TryGetValue(compilerName, out SourceFunctionName? mapped)
                            ? mapped.QualifiedName
                            : null;
                }

            case Expr.Var v:
                {
                    if (_functionSourceNames is not null
                        && _functionSourceNames.TryGetValue(v.Name, out SourceFunctionName? direct))
                    {
                        return direct.QualifiedName;
                    }

                    int? slot = Lookup(v.Name) switch
                    {
                        Binding.Local local => local.Slot,
                        Binding.Scheme scheme => scheme.Slot,
                        _ => null,
                    };
                    if (slot is int aliasSlot
                        && _letBindingValues.TryGetValue(aliasSlot, out Expr? aliasedValue)
                        && !ReferenceEquals(aliasedValue, root))
                    {
                        return ResolveCalleeQualifiedName(aliasedValue, depth + 1);
                    }

                    return null;
                }

            default:
                return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="expr"/> is a saturated call — curried or constructor-shaped — whose
    /// resolved callee identity is exactly <paramref name="qualifiedName"/>, and if so its arguments
    /// in application order. Used by fusions that must recognize a specific stdlib function by what
    /// it resolves to rather than by the source text spelling the call site happens to use.
    /// </summary>
    private bool TryMatchCallToQualifiedFunction(
        Expr expr,
        string qualifiedName,
        int expectedArgCount,
        [NotNullWhen(true)] out List<Expr>? arguments)
    {
        var collected = new List<Expr>();
        Expr root = CollectCallArgs(expr, collected);
        if (collected.Count == expectedArgCount
            && string.Equals(ResolveCalleeQualifiedName(root), qualifiedName, StringComparison.Ordinal))
        {
            arguments = collected;
            return true;
        }

        arguments = null;
        return false;
    }

    // Every plain top-level (non-recursive) function's own Expr.Lambda value, unconditionally — a
    // map/fold callback candidate resolved by name for the map-then-foldLeft fusion below needs the
    // body of even a pure arithmetic helper like `let increment value = value + 1`, which
    // _inlinableFunctions (Lowering.TopLevel.cs) deliberately excludes (its own gate keeps only
    // helpers that allocate). Populated once alongside the other top-level registries in
    // RegisterInlinableFunctions.
    private readonly Dictionary<string, Expr.Lambda> _totalityLambdasByName = new(StringComparer.Ordinal);

    private void RegisterTotalityCallbackCandidate(TopLevelItem.LetDecl let, Expr.Lambda lambda)
    {
        _totalityLambdasByName[let.Name] = lambda;
    }

    /// <summary>
    /// A map/fold callback's own (curried-stripped) innermost body, purely for the totality proof
    /// below — the callback itself is never relocated or re-lowered: TryLowerMapFoldLeftFusion lowers
    /// <c>f</c>/<c>g</c> exactly once, normally, as ordinary lambda parameters of the fused loop's own
    /// wrapper, so the fused loop calls them exactly the way a hand-written loop taking them as
    /// parameters would. Resolving a body here is only ever used to decide whether that's safe to do
    /// in a different relative order, never to inline or reproduce it.
    /// </summary>
    private Expr? TryResolveFusableCallbackBody(Expr expr, int arity)
    {
        Expr.Lambda? lambda = expr switch
        {
            Expr.Lambda literal => literal,
            Expr.Var v when _totalityLambdasByName.TryGetValue(v.Name, out Expr.Lambda? topLevel) => topLevel,
            _ => null,
        };
        return lambda is not null && CollectLambdaParams(lambda).Count == arity
            ? GetInnermostBody(lambda)
            : null;
    }

    // The restricted grammar a fused callback body must fall entirely within: literals, variable
    // reads (reading an already-bound value can never panic, diverge, or perform a capability effect,
    // regardless of what the callback closes over — nothing here is relocated, see
    // TryResolveFusableCallbackBody), the arithmetic/comparison/boolean operators, and `if`. Division
    // and remainder are excluded — both can panic on a zero divisor, which this proof must rule out
    // categorically, not case by case. Every other Expr shape (Call above all — trait dispatch for a
    // non-core-primitive operand is exactly the same hazard division is, see
    // IsFixedCoreBehaviorPrimitiveType) declines.
    private bool IsProvablyTotalExpr(Expr expr)
    {
        switch (expr)
        {
            case Expr.IntLit or Expr.BigIntLit or Expr.UIntLit or Expr.FloatLit
                or Expr.StrLit or Expr.RuneLit or Expr.BoolLit or Expr.Var:
                return true;

            case Expr.If ifExpr:
                return IsProvablyTotalExpr(ifExpr.Cond)
                    && IsProvablyTotalExpr(ifExpr.Then)
                    && IsProvablyTotalExpr(ifExpr.Else);

            case Expr.BitwiseNot notExpr:
                return IsProvablyTotalExpr(notExpr.Operand);

            case Expr.LogicalNot notExpr:
                return IsProvablyTotalExpr(notExpr.Operand);

            case Expr.Add e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.Subtract e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.Multiply e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.BitwiseAnd e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.BitwiseOr e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.BitwiseXor e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.ShiftLeft e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.ShiftRight e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.LogicalAnd e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.LogicalOr e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.GreaterThan e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.LessThan e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.GreaterOrEqual e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.LessOrEqual e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.Equal e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);
            case Expr.NotEqual e: return IsProvablyTotalExpr(e.Left) && IsProvablyTotalExpr(e.Right);

            default:
                return false;
        }
    }

    // The core Add/Subtract/Multiply/comparison/bitwise/logical implementations documented in
    // language.md are fixed for exactly these types — never overridden by a user Ashes.Trait
    // implementation, unlike a still-generic or named-ADT type. IsProvablyTotalExpr's operator
    // allowlist is sound only when every operand it touches resolves to one of these: for anything
    // else, `+` (say) could dispatch to a user-defined Add instance capable of panicking (Ashes.IO.panic
    // is unconditionally callable — no capability row gates it — so a total-looking operator body is
    // not proof against panic once a user type is in the mix).
    private bool IsFixedCoreBehaviorPrimitiveType(TypeRef type)
    {
        return Prune(type) is TypeRef.TInt or TypeRef.TFloat or TypeRef.TBigInt
            or TypeRef.TUInt or TypeRef.TStr or TypeRef.TRune or TypeRef.TBool;
    }

    // Binds an already-lowered value to a fresh source-level name, so a synthesized Expr fragment can
    // reference it as an ordinary Var without re-evaluating the expression that produced it. Mirrors
    // Lowering.Capabilities.cs's handle-result binding. Caller pops the pushed scope once the
    // synthesized fragment referencing it has been lowered. Reserved for a value only ever read
    // directly by the synthesized fragment itself — never captured into a further nested lambda that
    // fragment builds, which a manually-pushed Binding.Local is not safe for (see
    // project_mapfold_fusion_reuse_token_hazard in memory: a nested match's reuse-token analysis
    // mishandled a manually-bound capture in exactly that shape). f/g below are instead bound as
    // genuine Expr.Lambda parameters for this reason; only init/xs, read directly at the fused loop's
    // own call site and never captured further, use this.
    private void BindLoweredValueToFreshLocal(string name, int temp, TypeRef type)
    {
        int slot = NewLocal();
        Emit(new IrInst.StoreLocal(slot, temp));
        _scopes.Push(_scopes.Peek().SetItem(name, new Binding.Local(slot, type)));
    }

    private int _nextFusionId;

    private readonly record struct MapFoldFusionShape(
        Expr FExpr,
        Expr InitExpr,
        Expr MapRoot,
        Expr GExpr,
        Expr XsExpr,
        Expr GBody,
        Expr FBody);

    // Recognizes foldLeft(f)(init)(map(g)(xs)) — or its fold alias — by resolved callee identity on
    // both calls, and resolves f/g's bodies for the totality proof. Structural only: no lowering, no
    // side effects, safe to call speculatively and walk away from.
    private MapFoldFusionShape? TryRecognizeMapFoldFusionShape(Expr rootExpr, List<Expr> collectedArgs)
    {
        if (collectedArgs.Count != 3)
        {
            return null;
        }

        string? foldName = ResolveCalleeQualifiedName(rootExpr);
        if (foldName is not ("Ashes.Collection.List.foldLeft" or "Ashes.Collection.List.fold"))
        {
            return null;
        }

        var mapArgs = new List<Expr>();
        Expr mapRoot = CollectCallArgs(collectedArgs[2], mapArgs);
        if (mapArgs.Count != 2
            || !string.Equals(ResolveCalleeQualifiedName(mapRoot), "Ashes.Collection.List.map", StringComparison.Ordinal)
            || TryResolveFusableCallbackBody(mapArgs[0], 1) is not { } gBody
            || TryResolveFusableCallbackBody(collectedArgs[0], 2) is not { } fBody)
        {
            return null;
        }

        return new MapFoldFusionShape(collectedArgs[0], collectedArgs[1], mapRoot, mapArgs[0], mapArgs[1], gBody, fBody);
    }

    /// <summary>
    /// `Ashes.Collection.List.foldLeft(f)(init)(Ashes.Collection.List.map(g)(xs))` (or its `fold`
    /// alias) fused into a single pass applying g then f per element, never materializing the mapped
    /// list. f and g are lowered exactly once, normally, as ordinary parameters of the fused loop's
    /// own immediately-applied wrapper — the fused loop calls them exactly like the hand-written loop
    /// `let recursive go acc rest f g = match rest with | [] -> acc | head :: tail ->
    /// go(f(acc)(g(head)))(tail)(f)(g)` would, never relocating or re-lowering either body — so the
    /// only new argument this needs is the one map-then-fold's own reordering already demands:
    /// map-then-fold evaluates every g before any f, this interleaves them, which changes nothing
    /// observable only because both are proven total (IsProvablyTotalExpr/
    /// IsFixedCoreBehaviorPrimitiveType). init and xs are lowered eagerly, in their original relative
    /// order, before that proof completes (their own types are what it needs), so a declined fusion
    /// falls back to the original calls reapplied to the already-lowered values rather than evaluating
    /// either one twice.
    /// </summary>
    // A declined fusion's own fallback reapplies foldLeft/map to the already-lowered init/xs — an
    // expression that, structurally, resolves through exactly the same callee identities and so
    // matches TryRecognizeMapFoldFusionShape again. Left unguarded, lowering that fallback would
    // re-enter this method, decline for the identical reason (init/xs already have their final,
    // unchanging types), rebuild an equivalent fallback, and recurse forever. This flag makes every
    // call below the first one in a given attempt see "already declined" immediately, the same way a
    // shape genuinely outside the grammar would.
    private bool _loweringMapFoldFusionFallback;

    private (int, TypeRef)? TryLowerMapFoldLeftFusion(
        Expr rootExpr,
        List<Expr> collectedArgs,
        LoweredValueRequest request)
    {
        if (_loweringMapFoldFusionFallback
            || TryRecognizeMapFoldFusionShape(rootExpr, collectedArgs) is not { } shape
            || !IsProvablyTotalExpr(shape.GBody)
            || !IsProvablyTotalExpr(shape.FBody))
        {
            return null;
        }

        int fusionId = _nextFusionId++;
        (int initTemp, TypeRef initType) = LowerExpr(shape.InitExpr).AsPair();
        (int xsTemp, TypeRef xsType) = LowerExpr(shape.XsExpr).AsPair();
        bool provenTotal = IsFixedCoreBehaviorPrimitiveType(initType)
            && Prune(xsType) is TypeRef.TList xsList
            && IsFixedCoreBehaviorPrimitiveType(xsList.Element);

        string initName = $"__fuse_init_{fusionId}";
        string xsName = $"__fuse_xs_{fusionId}";
        BindLoweredValueToFreshLocal(initName, initTemp, initType);
        Expr synthesized;
        if (provenTotal)
        {
            BindLoweredValueToFreshLocal(xsName, xsTemp, xsType);
            synthesized = BuildFusedMapFoldWrapperExpr(fusionId, shape.FExpr, shape.GExpr, initName, xsName);
            (int fusedTemp, TypeRef fusedType) = LowerExpr(synthesized, request).AsPair();
            _scopes.Pop();
            _scopes.Pop();
            return (fusedTemp, fusedType);
        }

        BindLoweredValueToFreshLocal(xsName, xsTemp, xsType);
        synthesized = new Expr.Call(
            new Expr.Call(new Expr.Call(rootExpr, shape.FExpr), new Expr.Var(initName)),
            new Expr.Call(new Expr.Call(shape.MapRoot, shape.GExpr), new Expr.Var(xsName)));
        bool savedFallback = _loweringMapFoldFusionFallback;
        _loweringMapFoldFusionFallback = true;
        (int declinedTemp, TypeRef declinedType) = LowerExpr(synthesized, request).AsPair();
        _loweringMapFoldFusionFallback = savedFallback;
        _scopes.Pop();
        _scopes.Pop();
        return (declinedTemp, declinedType);
    }

    // (given (f) -> given (g) -> let recursive go acc rest = match rest with | [] -> acc |
    // head :: tail -> go(f(acc)(g(head)))(tail) in go(initName)(xsName))(fExpr)(gExpr) — f and g bind
    // as this wrapper's own ordinary lambda parameters, exactly the shape a hand-written version of
    // this loop taking f/g as parameters would use, rather than as manually-pushed scope values a
    // nested closure captures (see BindLoweredValueToFreshLocal's own note on why that matters).
    private static Expr BuildFusedMapFoldWrapperExpr(int fusionId, Expr fExpr, Expr gExpr, string initName, string xsName)
    {
        string fParam = $"__fuse_f_{fusionId}";
        string gParam = $"__fuse_g_{fusionId}";
        string goName = $"__fuse_go_{fusionId}";
        string freshAcc = $"__fuse_acc_{fusionId}";
        string freshRest = $"__fuse_rest_{fusionId}";
        string freshHead = $"__fuse_head_{fusionId}";
        string freshTail = $"__fuse_tail_{fusionId}";
        Expr mapped = new Expr.Call(new Expr.Var(gParam), new Expr.Var(freshHead));
        Expr nextAcc = new Expr.Call(new Expr.Call(new Expr.Var(fParam), new Expr.Var(freshAcc)), mapped);
        Expr consCase = new Expr.Call(new Expr.Call(new Expr.Var(goName), nextAcc), new Expr.Var(freshTail));
        Expr loopBody = new Expr.Match(
            new Expr.Var(freshRest),
            [
                new MatchCase(new Pattern.EmptyList(), new Expr.Var(freshAcc)),
                new MatchCase(new Pattern.Cons(new Pattern.Var(freshHead), new Pattern.Var(freshTail)), consCase),
            ]);
        Expr goValue = new Expr.Lambda(freshAcc, new Expr.Lambda(freshRest, loopBody));
        Expr goCall = new Expr.Call(
            new Expr.Call(new Expr.Var(goName), new Expr.Var(initName)),
            new Expr.Var(xsName));
        Expr wrapperBody = new Expr.LetRecursive(goName, goValue, goCall);
        Expr wrapper = new Expr.Lambda(fParam, new Expr.Lambda(gParam, wrapperBody));
        return new Expr.Call(new Expr.Call(wrapper, fExpr), gExpr);
    }
}
