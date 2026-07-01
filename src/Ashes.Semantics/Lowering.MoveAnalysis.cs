using Ashes.Frontend;

namespace Ashes.Semantics;

// Interprocedural move/linearity analysis for in-place-reuse copy elision.
//
// In-place reuse makes a fold's accumulator uniquely owned by deep-copying it at the fold
// function's entry, so the specialized `f$reuse` body may overwrite the accumulator's cells in
// place. That entry copy is the machinery's *only* mechanism for establishing uniqueness. When an
// outer loop threads a growing accumulator into an inner reuse fold, the inner fold's prologue copy
// re-executes on every re-entry and re-copies the whole structure — an O(re-entries × size) leak.
//
// This analysis proves, conservatively and whole-program, that the accumulator entering a fold is
// *already* uniquely owned at every external call site. When proven, the prologue copy is redundant
// (it would only duplicate an already-unique value) and is elided; the fold then receives exactly
// the precondition the copy used to guarantee. The default is always copy-stays: any shape the
// analysis cannot fully prove keeps the copy, so an incomplete analysis can only leak, never corrupt.
//
// Soundness rests on three checkable facts, no reuse-internals reasoning:
//   1. Move-linearity: at the call passing the accumulator, the argument has no other live
//      reference in the enclosing function (used at most once on any execution path, never captured
//      by a nested lambda). So overwriting it in place cannot corrupt a concurrently-live alias.
//   2. Transitivity: a Var argument is unique only if it is itself a move-safe accumulator
//      parameter of its enclosing function — recursively, down to a base seed. A greatest-fixpoint
//      computed on demand (cycles resolve to "not proven" = keep copy).
//   3. Seed safety: the base case is a value that is the *sole nullary constructor* of its type
//      (e.g. `Ashes.Map.empty = Empty`). Such a cell holds only its tag; the only reuse token it
//      can produce is a 0-field token, which — in a well-typed program — is consumed to rebuild the
//      same unique nullary constructor, writing the identical tag (a no-op). It therefore can never
//      be observably mutated, so it is safe to move even when shared. Field-bearing seeds have no
//      such guarantee and are not accepted.
//   4. Full visibility: a fold (or an intermediate function whose parameter is threaded) is only
//      considered if its name never escapes as a value — it appears solely as the head of a
//      saturated direct call — so the call-site census is provably complete.
public sealed partial class Lowering
{
    // Per top-level function: its curried parameter names (in order) and its innermost body.
    private readonly Dictionary<string, (List<string> Params, Expr Body)> _maFuncs = new(StringComparer.Ordinal);

    // Every top-level value binding's right-hand side (used to resolve accumulator seeds).
    private readonly Dictionary<string, Expr> _maValueRhs = new(StringComparer.Ordinal);

    // Saturated direct call sites, keyed by callee name: (enclosing function name, flattened args).
    private readonly Dictionary<string, List<(string Enclosing, List<Expr> Args)>> _maCallSites = new(StringComparer.Ordinal);

    // Function names that appear anywhere other than as the head of a saturated direct call. Their
    // call-site census is not provably complete, so they are never treated as move-safe.
    private readonly HashSet<string> _maEscaped = new(StringComparer.Ordinal);

    // Memoization for the on-demand greatest fixpoint. A (func,param) currently being resolved is in
    // _maInProgress; re-encountering it (a cycle) yields "not proven" — the sound under-approximation.
    private readonly Dictionary<(string, string), bool> _maMoveSafeMemo = new();
    private readonly HashSet<(string, string)> _maInProgress = new();

    private bool _maAnalyzed;

    // The fully-desugared program body. Used as the "enclosing body" for the local-let move check at
    // top-level call sites (enclosing == ""), which have no registered function frame: a top-level
    // `let seed = <fresh> in ... F(seed) ...` binds `seed` on this spine, and move-linearity over the
    // whole program is the (stronger) proof that no other live reference exists.
    private Expr? _maBody;

    // Names bound by more than one let/letrec in the desugared tree. A duplicated name cannot be
    // resolved unambiguously, so such names are never treated as move-safe (their pooled call sites
    // would be unsound); they are also treated as escaped.
    private readonly HashSet<string> _maAmbiguous = new(StringComparer.Ordinal);

    // Result-freshness summary (CO-2 higher-order seeds): per registered function, true when its
    // result is provably a uniquely-owned, freshly-allocated value on every execution path — every
    // heap cell reachable from the return value is either freshly allocated by that function or a
    // no-op-safe sole-nullary constructor cell, and no pre-existing/aliased heap value is embedded
    // into a heap-typed field. Computed once as a monotone greatest fixpoint (all functions assumed
    // fresh, retracted when a concrete non-fresh shape is found). Used to admit a fold accumulator
    // seeded by such a function's *result* (`let s = build(n) in fold(s)` or `fold(build(n))`) as a
    // move, without needing the return-value ownership reasoning of the broader ownership milestone.
    private readonly Dictionary<string, bool> _maResultFresh = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds the whole-program call-site census and function tables used by
    /// <see cref="IsReuseAccumulatorMoveSafe"/> over the fully desugared program expression (which
    /// contains the stitched stdlib bindings, the user's top-level declarations, and the trailing
    /// expression as one nested let chain). Idempotent.
    /// </summary>
    private void AnalyzeReuseCopyElision(Expr desugaredBody)
    {
        _maFuncs.Clear();
        _maValueRhs.Clear();
        _maCallSites.Clear();
        _maEscaped.Clear();
        _maAmbiguous.Clear();
        _maMoveSafeMemo.Clear();
        _maInProgress.Clear();
        _maResultFresh.Clear();
        _maBody = desugaredBody;

        RegisterBindings(desugaredBody);

        // Duplicated names are unusable: drop them from the function table and mark escaped so no
        // move-safety proof can rely on them.
        foreach (var name in _maAmbiguous)
        {
            _maFuncs.Remove(name);
            _maValueRhs.Remove(name);
            _maEscaped.Add(name);
        }

        CollectCallsAndEscapes(desugaredBody, "");
        ComputeResultFreshness();
        _maAnalyzed = true;
    }

    private static Expr StripOrSelf(Expr value)
    {
        try
        {
            return StripModuleAliasPrefix(value);
        }
        catch (System.NotSupportedException)
        {
            return value;
        }
    }

    /// <summary>
    /// Records every let/letrec binding in the desugared tree: lambda-valued ones as functions
    /// (params + innermost body), all as value RHS (for seed resolution). Flags duplicate names.
    /// </summary>
    private void RegisterBindings(Expr e)
    {
        switch (e)
        {
            case Expr.Let l:
                RegisterOneBinding(l.Name, l.Value);
                RegisterBindings(l.Value);
                RegisterBindings(l.Body);
                return;
            case Expr.LetRec lr:
                RegisterOneBinding(lr.Name, lr.Value);
                RegisterBindings(lr.Value);
                RegisterBindings(lr.Body);
                return;
            case Expr.LetResult lres:
                RegisterOneBinding(lres.Name, lres.Value);
                RegisterBindings(lres.Value);
                RegisterBindings(lres.Body);
                return;
            case Expr.Lambda lam:
                RegisterBindings(lam.Body);
                return;
            case Expr.If i:
                RegisterBindings(i.Cond);
                RegisterBindings(i.Then);
                RegisterBindings(i.Else);
                return;
            case Expr.Match m:
                RegisterBindings(m.Value);
                foreach (var c in m.Cases)
                {
                    RegisterBindings(c.Body);
                    if (c.Guard is not null)
                    {
                        RegisterBindings(c.Guard);
                    }
                }

                return;
            case Expr.Call c:
                RegisterBindings(c.Func);
                RegisterBindings(c.Arg);
                return;
            default:
                foreach (var child in EnumerateChildren(e))
                {
                    RegisterBindings(child);
                }

                return;
        }
    }

    private void RegisterOneBinding(string name, Expr value)
    {
        if (_maValueRhs.ContainsKey(name) || _maFuncs.ContainsKey(name))
        {
            _maAmbiguous.Add(name);
            return;
        }

        var stripped = StripOrSelf(value);
        _maValueRhs[name] = stripped;
        if (stripped is Expr.Lambda lam)
        {
            _maFuncs[name] = (CollectLambdaParams(lam), GetInnermostBody(lam));
        }
    }

    private static IEnumerable<Expr> EnumerateChildren(Expr e)
    {
        switch (e)
        {
            case Expr.Add x: yield return x.Left; yield return x.Right; break;
            case Expr.Subtract x: yield return x.Left; yield return x.Right; break;
            case Expr.Multiply x: yield return x.Left; yield return x.Right; break;
            case Expr.Divide x: yield return x.Left; yield return x.Right; break;
            case Expr.BitwiseAnd x: yield return x.Left; yield return x.Right; break;
            case Expr.BitwiseOr x: yield return x.Left; yield return x.Right; break;
            case Expr.BitwiseXor x: yield return x.Left; yield return x.Right; break;
            case Expr.ShiftLeft x: yield return x.Left; yield return x.Right; break;
            case Expr.ShiftRight x: yield return x.Left; yield return x.Right; break;
            case Expr.BitwiseNot x: yield return x.Operand; break;
            case Expr.GreaterThan x: yield return x.Left; yield return x.Right; break;
            case Expr.LessThan x: yield return x.Left; yield return x.Right; break;
            case Expr.GreaterOrEqual x: yield return x.Left; yield return x.Right; break;
            case Expr.LessOrEqual x: yield return x.Left; yield return x.Right; break;
            case Expr.Equal x: yield return x.Left; yield return x.Right; break;
            case Expr.NotEqual x: yield return x.Left; yield return x.Right; break;
            case Expr.ResultPipe x: yield return x.Left; yield return x.Right; break;
            case Expr.ResultMapErrorPipe x: yield return x.Left; yield return x.Right; break;
            case Expr.Cons x: yield return x.Head; yield return x.Tail; break;
            case Expr.Await x: yield return x.Task; break;
            case Expr.TupleLit x:
                foreach (var el in x.Elements) { yield return el; }

                break;
            case Expr.ListLit x:
                foreach (var el in x.Elements) { yield return el; }

                break;
            case Expr.RecordLit x:
                foreach (var (_, fv) in x.Fields) { yield return fv; }

                break;
            case Expr.RecordUpdate x:
                yield return x.Target;
                foreach (var (_, uv) in x.Updates) { yield return uv; }

                break;
            default:
                break;
        }
    }

    /// <summary>
    /// True when the prologue deep-copy of accumulator parameter <paramref name="accParam"/> of fold
    /// <paramref name="funcName"/> can be safely elided: the accumulator is provably uniquely owned
    /// at every external call site. Conservative — returns false on any uncertainty.
    /// </summary>
    private bool IsReuseAccumulatorMoveSafe(string funcName, string accParam)
    {
        if (!_maAnalyzed)
        {
            return false;
        }

        return IsParamMoveSafe(funcName, accParam);
    }

    /// <summary>
    /// Greatest-fixpoint node: the value bound to parameter <paramref name="param"/> of top-level
    /// function <paramref name="func"/> is uniquely owned at every invocation. Computed on demand
    /// with cycle-breaking (a cycle resolves to false — the sound under-approximation).
    /// </summary>
    private bool IsParamMoveSafe(string func, string param)
    {
        var key = (func, param);
        if (_maMoveSafeMemo.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (!_maInProgress.Add(key))
        {
            return false; // cycle — not proven this pass
        }

        bool result = ComputeParamMoveSafe(func, param);
        _maInProgress.Remove(key);
        _maMoveSafeMemo[key] = result;
        return result;
    }

    private bool ComputeParamMoveSafe(string func, string param)
    {
        // The function must be fully visible (never escapes as a value) and have a known parameter
        // list, otherwise its call sites are not provably complete.
        if (_maEscaped.Contains(func) || !_maFuncs.TryGetValue(func, out var info))
        {
            return false;
        }

        int paramIndex = info.Params.IndexOf(param);
        if (paramIndex < 0)
        {
            return false;
        }

        if (!_maCallSites.TryGetValue(func, out var sites))
        {
            return false; // no observed call site — never proven (dead or hidden)
        }

        bool sawExternal = false;
        foreach (var (enclosing, args) in sites)
        {
            if (string.Equals(enclosing, func, StringComparison.Ordinal))
            {
                continue; // self-recursion is the TCO back-edge, not an external entry
            }

            if (paramIndex >= args.Count)
            {
                return false; // under-applied at this site — cannot map the argument
            }

            sawExternal = true;
            if (!ArgIsMove(args[paramIndex], enclosing))
            {
                return false;
            }
        }

        return sawExternal;
    }

    /// <summary>
    /// True when argument <paramref name="arg"/>, passed from function <paramref name="enclosing"/>,
    /// denotes a uniquely-owned value that is moved (not retained) here: either a safe nullary seed,
    /// or a move-linear reference to a move-safe accumulator parameter of the enclosing function.
    /// </summary>
    private bool ArgIsMove(Expr arg, string enclosing)
    {
        if (IsNullarySeed(arg, new HashSet<string>(StringComparer.Ordinal)))
        {
            return true;
        }

        // A syntactically fully-fresh allocation (a saturated constructor application / literal /
        // aggregate literal built solely from constructors and literals, with NO variable reference
        // anywhere in it) is unaliased by construction: every reachable cell is freshly allocated by
        // this very expression and reachable only through this single argument reference, so moving
        // it into a reuse fold can never corrupt a concurrently-live value. Unlike a nullary seed
        // (safe even when *shared*), the guarantee here is uniqueness-by-construction, so any nullary
        // constructor — not only the sole one — is admissible, and non-nullary shapes are covered.
        if (IsFullyFreshConstruction(arg))
        {
            return true;
        }

        // (CO-2 higher-order seed) A saturated call to a result-fresh function written inline at the
        // call site: its result is a uniquely-owned freshly-allocated value (see IsResultFresh), and a
        // freshly-produced result reachable only through this one argument reference is inherently a
        // move — used once, unaliased — exactly like an inline fresh construction above.
        if (IsFreshResultCall(arg))
        {
            return true;
        }

        if (arg is Expr.Var v)
        {
            bool haveFunc = _maFuncs.TryGetValue(enclosing, out var encInfo);

            // (i) A move-linear reference to a move-safe accumulator parameter of the enclosing
            // function (the transitive, interprocedural step).
            if (haveFunc
                && encInfo.Params.Contains(v.Name)
                && IsMoveLinear(v.Name, encInfo.Body)
                && IsParamMoveSafe(enclosing, v.Name))
            {
                return true;
            }

            // (ii) Richer aliasing (CO-2 increment): a `Var` that is NOT syntactically fresh at the
            // call site, but is bound by a `let` LOCAL to the enclosing scope to a fully-fresh
            // construction, and is move-linear here (used at most once on any path, never captured).
            // The `let` confines the name's scope to this body, so move-linearity there proves no
            // other live reference exists anywhere; the fresh construction proves the bound value is
            // unaliased and free of internal sharing. Together the value is uniquely owned and
            // dead-after-this-use — a sound move — even though the freshness is at the binding site
            // rather than at the call site. Only accepted when the name is unambiguous program-wide (a
            // single binding), so the located RHS is definitive. At a top-level call site (no
            // registered function frame) the enclosing body is the whole desugared program; a
            // top-level `let seed = <fresh>` lives on its spine and move-linearity over the whole
            // program is the stronger proof of unique ownership.
            Expr? encBody = haveFunc ? encInfo.Body : (enclosing.Length == 0 ? _maBody : null);
            if (encBody is not null
                && !_maAmbiguous.Contains(v.Name)
                && TryFindLocalLet(v.Name, encBody) is var (boundRhs, boundScope)
                && boundRhs is not null
                && boundScope is not null
                // Fresh by construction, or (CO-2 higher-order seed) the result of a result-fresh
                // function call — both give a uniquely-owned, internally-unshared bound value.
                && (IsFullyFreshConstruction(boundRhs) || IsFreshResultCall(boundRhs))
                // Move-linear within the binding's own scope (its `let` body): used at most once on
                // any path there, never captured. Counting in the scope — not the whole enclosing
                // body — is essential: the whole-body count would stop at this very definition
                // (treating it as a shadow) and spuriously report zero uses.
                && MaxPathOccurrences(v.Name, boundScope) <= 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Locates a non-recursive <c>let</c>/<c>let-result</c> binding of <paramref name="name"/> within
    /// <paramref name="body"/> and returns its right-hand side together with the binding's <b>scope</b>
    /// (the <c>let</c> body, over which <paramref name="name"/> is live). Returns <c>(null, null)</c>
    /// when no such binding exists. Traverses control-flow structure but never descends into a nested
    /// <c>Lambda</c> body — a nested lambda is a separate function scope, so a binding inside it is not
    /// local to this function. Recursive (<c>let rec</c>) bindings are excluded: their RHS is
    /// self-referential and never a fresh construction. Returning the scope (not the whole enclosing
    /// body) is what lets the caller count uses correctly — a whole-body occurrence count would stop
    /// at this very definition (treating it as a shadow) and report zero uses.
    /// </summary>
    private static (Expr? Rhs, Expr? Scope) TryFindLocalLet(string name, Expr body)
    {
        switch (body)
        {
            case Expr.Let l:
                if (string.Equals(l.Name, name, StringComparison.Ordinal))
                {
                    return (l.Value, l.Body);
                }

                return FirstFound(TryFindLocalLet(name, l.Value), () => TryFindLocalLet(name, l.Body));

            case Expr.LetResult lr:
                if (string.Equals(lr.Name, name, StringComparison.Ordinal))
                {
                    return (lr.Value, lr.Body);
                }

                return FirstFound(TryFindLocalLet(name, lr.Value), () => TryFindLocalLet(name, lr.Body));

            case Expr.LetRec lrec:
                // A self-referential binding is never a fresh construction; only search its subtrees.
                if (string.Equals(lrec.Name, name, StringComparison.Ordinal))
                {
                    return (null, null);
                }

                return FirstFound(TryFindLocalLet(name, lrec.Value), () => TryFindLocalLet(name, lrec.Body));

            case Expr.If i:
                return FirstFound(
                    TryFindLocalLet(name, i.Cond),
                    () => FirstFound(TryFindLocalLet(name, i.Then), () => TryFindLocalLet(name, i.Else)));

            case Expr.Match m:
                {
                    var found = TryFindLocalLet(name, m.Value);
                    if (found.Rhs is not null)
                    {
                        return found;
                    }

                    foreach (var c in m.Cases)
                    {
                        // A pattern binding of the same name shadows the let we are after in that arm.
                        if (PatternBinds(c.Pattern, name))
                        {
                            continue;
                        }

                        found = TryFindLocalLet(name, c.Body);
                        if (found.Rhs is not null)
                        {
                            return found;
                        }

                        if (c.Guard is not null)
                        {
                            found = TryFindLocalLet(name, c.Guard);
                            if (found.Rhs is not null)
                            {
                                return found;
                            }
                        }
                    }

                    return (null, null);
                }

            case Expr.Call c:
                return FirstFound(TryFindLocalLet(name, c.Func), () => TryFindLocalLet(name, c.Arg));

            // A nested lambda is a separate function scope: do NOT descend (a binding inside it is not
            // local to this function, and this function's `name` cannot be bound there).
            case Expr.Lambda:
                return (null, null);

            default:
                foreach (var child in EnumerateChildren(body))
                {
                    var found = TryFindLocalLet(name, child);
                    if (found.Rhs is not null)
                    {
                        return found;
                    }
                }

                return (null, null);
        }
    }

    private static (Expr? Rhs, Expr? Scope) FirstFound((Expr? Rhs, Expr? Scope) first, System.Func<(Expr? Rhs, Expr? Scope)> second)
    {
        return first.Rhs is not null ? first : second();
    }

    /// <summary>
    /// True when <paramref name="arg"/> resolves to the sole nullary constructor of its type — a
    /// value whose cell can never be observably overwritten by in-place reuse (see the file header).
    /// Follows top-level value aliases (e.g. <c>Ashes.Map.empty → Empty</c>), cycle-guarded.
    /// </summary>
    private bool IsNullarySeed(Expr arg, HashSet<string> visiting)
    {
        switch (arg)
        {
            case Expr.Var v:
                if (_constructorSymbols.TryGetValue(v.Name, out var ctor))
                {
                    return IsSoleNullaryConstructor(ctor);
                }

                if (visiting.Add(v.Name) && _maValueRhs.TryGetValue(v.Name, out var rhs))
                {
                    return IsNullarySeed(rhs, visiting);
                }

                return false;

            case Expr.QualifiedVar qv:
                var resolved = ResolveSpecializableCalleeName(qv);
                if (resolved is not null && visiting.Add(resolved) && _maValueRhs.TryGetValue(resolved, out var qrhs))
                {
                    return IsNullarySeed(qrhs, visiting);
                }

                return false;

            default:
                return false;
        }
    }

    private bool IsSoleNullaryConstructor(ConstructorSymbol ctor)
    {
        if (ctor.Arity != 0 || !_typeSymbols.TryGetValue(ctor.ParentType, out var typeSym))
        {
            return false;
        }

        int nullaryCount = 0;
        foreach (var c in typeSym.Constructors)
        {
            if (c.Arity == 0)
            {
                nullaryCount++;
            }
        }

        return nullaryCount == 1;
    }

    /// <summary>
    /// True when <paramref name="arg"/> is a syntactically fully-fresh allocation: a saturated
    /// constructor application, a scalar/string literal, or a tuple/list/cons/record literal, whose
    /// every sub-expression is itself fully fresh. Crucially it contains <b>no variable reference</b>
    /// (no <c>Var</c>/<c>QualifiedVar</c>, no <c>Call</c> to a non-constructor) — a variable could
    /// alias shared data or introduce internal sharing (e.g. <c>let x = Node(..) in Node(0, x, x)</c>),
    /// which reuse could then corrupt. With no variables the value is a fresh tree with no internal
    /// sharing, reachable only through this one argument reference, hence a sound move. A bare
    /// (unapplied) <c>Var</c> is never fresh here — nullary seeds go through <see cref="IsNullarySeed"/>.
    /// </summary>
    private bool IsFullyFreshConstruction(Expr arg)
    {
        switch (arg)
        {
            case Expr.IntLit:
            case Expr.UIntLit:
            case Expr.FloatLit:
            case Expr.StrLit:
            case Expr.BoolLit:
                return true;

            // A bare name is fresh only when it is the sole nullary constructor of its type — a
            // 0-field cell whose reuse-overwrite is a no-op even if shared (the seed rule). Any other
            // variable may alias shared data, so it breaks freshness.
            case Expr.Var:
            case Expr.QualifiedVar:
                return IsNullarySeed(arg, new HashSet<string>(StringComparer.Ordinal));

            case Expr.TupleLit t:
                return t.Elements.All(IsFullyFreshConstruction);
            case Expr.ListLit lst:
                return lst.Elements.All(IsFullyFreshConstruction);
            case Expr.Cons cons:
                return IsFullyFreshConstruction(cons.Head) && IsFullyFreshConstruction(cons.Tail);
            case Expr.RecordLit rec:
                return rec.Fields.All(f => IsFullyFreshConstruction(f.Value));

            case Expr.Call:
                {
                    var args = new List<Expr>();
                    var head = CollectCallArgs(arg, args);
                    // Only a saturated application of a data constructor is a fresh allocation; any
                    // other call may return an aliased/shared (or reuse-rewritten) value.
                    if (head is Expr.Var hv
                        && _constructorSymbols.TryGetValue(hv.Name, out var ctor)
                        && args.Count == ctor.Arity)
                    {
                        return args.All(IsFullyFreshConstruction);
                    }

                    return false;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// Computes the result-freshness summary (<see cref="_maResultFresh"/>) as a monotone greatest
    /// fixpoint: every registered function starts assumed result-fresh, and any function whose body is
    /// found to produce a possibly-non-fresh value on some path is retracted, until no more retract.
    /// Starting optimistic (all-true) and only retracting is what lets a recursive builder qualify —
    /// its recursive self-call in a fresh field is read as fresh under the current assumption, so the
    /// only way a function stays fresh is if every *concrete* (non-recursive) shape it can return is
    /// itself fresh. A non-fresh value can only enter through a directly-checked shape (a bare
    /// parameter/global reference, or a heap-typed constructor field holding a non-fresh argument), so
    /// the fixpoint is sound regardless of recursion.
    /// </summary>
    private void ComputeResultFreshness()
    {
        _maResultFresh.Clear();
        foreach (var name in _maFuncs.Keys)
        {
            _maResultFresh[name] = true;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (name, info) in _maFuncs)
            {
                if (!_maResultFresh[name])
                {
                    continue; // already retracted — monotone, never revived
                }

                if (!ResultShapeFresh(info.Body))
                {
                    _maResultFresh[name] = false;
                    changed = true;
                }
            }
        }
    }

    /// <summary>
    /// True when <paramref name="e"/>, as the returned value of a function body, is provably a
    /// uniquely-owned freshly-allocated value under the current <see cref="_maResultFresh"/>
    /// assumption: every heap cell it can reach is freshly allocated here (or a no-op-safe sole-nullary
    /// constructor cell), and no pre-existing/aliased heap value is embedded into a heap-typed field.
    /// A bare parameter/global reference is fresh only when it is the sole nullary constructor of its
    /// type — a returned parameter, pattern-bound sub-value, or shared global otherwise breaks
    /// freshness (it may alias a value the reuse fold would overwrite in place).
    /// </summary>
    private bool ResultShapeFresh(Expr e)
    {
        switch (e)
        {
            case Expr.IntLit:
            case Expr.UIntLit:
            case Expr.FloatLit:
            case Expr.StrLit:
            case Expr.BoolLit:
                return true;

            // A bare name is fresh only when it denotes the sole nullary constructor of its type. Any
            // other variable — a parameter, a pattern-bound sub-value (e.g. `match t with Node(l,_,_)
            // -> l`), or a shared top-level binding — may alias a pre-existing heap value.
            case Expr.Var:
            case Expr.QualifiedVar:
                return IsNullarySeed(e, new HashSet<string>(StringComparer.Ordinal));

            // Control flow: the returned value is one of the arms, so every arm must be fresh. The
            // scrutinee/condition is not part of the returned value.
            case Expr.If i:
                return ResultShapeFresh(i.Then) && ResultShapeFresh(i.Else);
            case Expr.Match m:
                return m.Cases.Count > 0 && m.Cases.All(c => ResultShapeFresh(c.Body));

            // The returned value is the let body. A bare reference to the let-bound name inside the
            // body is rejected by the Var case above (it is not a constructor), so a body that embeds
            // the bound value into a heap field is conservatively declined — sound, only loses cases.
            case Expr.Let l:
                return ResultShapeFresh(l.Body);
            case Expr.LetResult lr:
                return ResultShapeFresh(lr.Body);
            case Expr.LetRec lrec:
                return ResultShapeFresh(lrec.Body);

            // Aggregate/list literals whose element types are not individually known here: require
            // every element fresh (conservative — a copy-typed element written fresh, e.g. a literal,
            // still passes; a bare heap reference does not).
            case Expr.Cons cons:
                return ResultShapeFresh(cons.Head) && ResultShapeFresh(cons.Tail);
            case Expr.ListLit lst:
                return lst.Elements.All(ResultShapeFresh);
            case Expr.TupleLit t:
                return t.Elements.All(ResultShapeFresh);
            case Expr.RecordLit rec:
                return rec.Fields.All(f => ResultShapeFresh(f.Value));

            case Expr.Call:
                return CallShapeFresh(e);

            default:
                return false;
        }
    }

    /// <summary>
    /// Result-freshness of a call expression: either a saturated data-constructor application (a fresh
    /// cell whose heap-typed fields must hold fresh arguments — copy-typed fields hold scalars stored
    /// inline and cannot alias a cell the fold overwrites, so they are unconstrained), or a saturated
    /// call to a function that is itself result-fresh (its result is fresh by the summary, for *any*
    /// arguments — a result-fresh function provably never embeds a heap argument into a heap-typed
    /// field of its result, so the argument shapes are irrelevant here).
    /// </summary>
    private bool CallShapeFresh(Expr e)
    {
        var args = new List<Expr>();
        var head = CollectCallArgs(e, args);

        // (1) Saturated data-constructor application: a freshly-allocated cell. Only heap-typed fields
        // can carry an aliasing hazard; copy-typed fields (Int/Float/Bool/…) hold their value inline.
        if (head is Expr.Var hv
            && _constructorSymbols.TryGetValue(hv.Name, out var ctor)
            && args.Count == ctor.Arity)
        {
            for (int i = 0; i < args.Count; i++)
            {
                bool copyField = ctor.ParameterTypes.Count == ctor.Arity
                    && BuiltinRegistry.IsCopyType(ctor.ParameterTypes[i]);
                if (!copyField && !ResultShapeFresh(args[i]))
                {
                    return false;
                }
            }

            return true;
        }

        // (2) Saturated call to a result-fresh function: fresh result regardless of arguments.
        string? name = head switch
        {
            Expr.Var v => v.Name,
            Expr.QualifiedVar => ResolveSpecializableCalleeName(head),
            _ => null,
        };

        return name is not null
            && !_maAmbiguous.Contains(name)
            && _maFuncs.TryGetValue(name, out var info)
            && args.Count == info.Params.Count
            && _maResultFresh.TryGetValue(name, out var fresh)
            && fresh;
    }

    /// <summary>
    /// True when <paramref name="arg"/> is a saturated call to a result-fresh function — a
    /// higher-order seed whose result is a uniquely-owned, freshly-allocated value (see
    /// <see cref="ComputeResultFreshness"/>). A saturated constructor application is deliberately
    /// excluded here (it is already covered by <see cref="IsFullyFreshConstruction"/>); this predicate
    /// is specifically the non-constructor case the fresh-construction rule cannot see through.
    /// </summary>
    private bool IsFreshResultCall(Expr arg)
    {
        if (arg is not Expr.Call)
        {
            return false;
        }

        var args = new List<Expr>();
        var head = CollectCallArgs(arg, args);

        // A constructor application is not a "result call" — IsFullyFreshConstruction handles it.
        if (head is Expr.Var cv && _constructorSymbols.ContainsKey(cv.Name))
        {
            return false;
        }

        string? name = head switch
        {
            Expr.Var v => v.Name,
            Expr.QualifiedVar => ResolveSpecializableCalleeName(head),
            _ => null,
        };

        return name is not null
            && !_maAmbiguous.Contains(name)
            && _maFuncs.TryGetValue(name, out var info)
            && args.Count == info.Params.Count
            && _maResultFresh.TryGetValue(name, out var fresh)
            && fresh;
    }

    /// <summary>
    /// True when <paramref name="name"/> is used at most once on any execution path through
    /// <paramref name="body"/> and is never captured by a nested lambda — so on the path that moves
    /// it into a call, no other live reference to it exists.
    /// </summary>
    private static bool IsMoveLinear(string name, Expr body)
    {
        return MaxPathOccurrences(name, body) <= 1;
    }

    // Sentinel forcing decline: any capture by a nested lambda, or an unmodeled node.
    private const int OccEscape = 1 << 20;

    /// <summary>
    /// Maximum number of times <paramref name="name"/> can be evaluated on a single execution path
    /// through <paramref name="e"/>. Branches take the max of their arms; sequential sub-expressions
    /// sum. A capturing lambda or an unmodeled node returns <see cref="OccEscape"/> (forces decline).
    /// A binding that shadows <paramref name="name"/> stops counting in the shadowed scope.
    /// </summary>
    private static int MaxPathOccurrences(string name, Expr e)
    {
        switch (e)
        {
            case Expr.Var v:
                return string.Equals(v.Name, name, StringComparison.Ordinal) ? 1 : 0;

            case Expr.QualifiedVar:
            case Expr.IntLit:
            case Expr.UIntLit:
            case Expr.FloatLit:
            case Expr.StrLit:
            case Expr.BoolLit:
                return 0;

            case Expr.If i:
                return MaxPathOccurrences(name, i.Cond)
                    + System.Math.Max(MaxPathOccurrences(name, i.Then), MaxPathOccurrences(name, i.Else));

            case Expr.Match m:
                {
                    int worst = 0;
                    foreach (var c in m.Cases)
                    {
                        // A pattern binding of the same name shadows it inside that arm.
                        int arm = PatternBinds(c.Pattern, name)
                            ? 0
                            : MaxPathOccurrences(name, c.Body)
                                + (c.Guard is null ? 0 : MaxPathOccurrences(name, c.Guard));
                        worst = System.Math.Max(worst, arm);
                    }

                    return MaxPathOccurrences(name, m.Value) + worst;
                }

            case Expr.Let l:
                // The value is evaluated in the outer scope; the body sees the new binding, which
                // shadows `name` when the names coincide.
                return MaxPathOccurrences(name, l.Value)
                    + (string.Equals(l.Name, name, StringComparison.Ordinal) ? 0 : MaxPathOccurrences(name, l.Body));

            case Expr.LetResult lr:
                return MaxPathOccurrences(name, lr.Value)
                    + (string.Equals(lr.Name, name, StringComparison.Ordinal) ? 0 : MaxPathOccurrences(name, lr.Body));

            case Expr.LetRec lrec:
                // Recursive: the bound name is in scope for both value and body.
                return string.Equals(lrec.Name, name, StringComparison.Ordinal)
                    ? 0
                    : MaxPathOccurrences(name, lrec.Value) + MaxPathOccurrences(name, lrec.Body);

            case Expr.Lambda lam:
                // A capture keeps the value live beyond a single evaluation and may alias it.
                if (string.Equals(lam.ParamName, name, StringComparison.Ordinal))
                {
                    return 0; // shadowed
                }

                return MaxPathOccurrences(name, lam.Body) > 0 ? OccEscape : 0;

            case Expr.Call c:
                return MaxPathOccurrences(name, c.Func) + MaxPathOccurrences(name, c.Arg);

            case Expr.Add x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.Subtract x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.Multiply x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.Divide x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.BitwiseAnd x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.BitwiseOr x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.BitwiseXor x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.ShiftLeft x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.ShiftRight x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.BitwiseNot x: return MaxPathOccurrences(name, x.Operand);
            case Expr.GreaterThan x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.LessThan x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.GreaterOrEqual x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.LessOrEqual x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.Equal x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.NotEqual x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.ResultPipe x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);
            case Expr.ResultMapErrorPipe x: return MaxPathOccurrences(name, x.Left) + MaxPathOccurrences(name, x.Right);

            case Expr.Cons cons:
                return MaxPathOccurrences(name, cons.Head) + MaxPathOccurrences(name, cons.Tail);

            case Expr.Await aw:
                return MaxPathOccurrences(name, aw.Task);

            case Expr.TupleLit t:
                return SumOccurrences(name, t.Elements);
            case Expr.ListLit lst:
                return SumOccurrences(name, lst.Elements);

            case Expr.RecordLit rec:
                {
                    int total = 0;
                    foreach (var (_, fv) in rec.Fields)
                    {
                        total += MaxPathOccurrences(name, fv);
                    }

                    return total;
                }

            case Expr.RecordUpdate ru:
                {
                    int total = MaxPathOccurrences(name, ru.Target);
                    foreach (var (_, uv) in ru.Updates)
                    {
                        total += MaxPathOccurrences(name, uv);
                    }

                    return total;
                }

            default:
                // Unmodeled node — decline conservatively.
                return OccEscape;
        }
    }

    private static int SumOccurrences(string name, IReadOnlyList<Expr> exprs)
    {
        int total = 0;
        foreach (var e in exprs)
        {
            total += MaxPathOccurrences(name, e);
        }

        return total;
    }

    private static bool PatternBinds(Pattern p, string name)
    {
        switch (p)
        {
            case Pattern.Var v:
                return string.Equals(v.Name, name, StringComparison.Ordinal);
            case Pattern.Constructor c:
                foreach (var sub in c.Patterns)
                {
                    if (PatternBinds(sub, name))
                    {
                        return true;
                    }
                }

                return false;
            case Pattern.Tuple t:
                foreach (var sub in t.Elements)
                {
                    if (PatternBinds(sub, name))
                    {
                        return true;
                    }
                }

                return false;
            case Pattern.Cons cons:
                return PatternBinds(cons.Head, name) || PatternBinds(cons.Tail, name);
            default:
                return false;
        }
    }

    /// <summary>
    /// Records saturated direct call sites and marks any function name that appears in a non-call-head
    /// position (bare value, partial/over-application) as escaped.
    /// </summary>
    private void CollectCallsAndEscapes(Expr e, string enclosing)
    {
        switch (e)
        {
            case Expr.Call:
                {
                    var args = new List<Expr>();
                    var root = CollectCallArgs(e, args);
                    string? calleeName = root switch
                    {
                        Expr.Var v => v.Name,
                        Expr.QualifiedVar qv => ResolveSpecializableCalleeName(qv),
                        _ => null,
                    };

                    if (calleeName is not null
                        && _maFuncs.TryGetValue(calleeName, out var callee)
                        && args.Count == callee.Params.Count)
                    {
                        // A complete, saturated call to a known function: record it and recurse into
                        // the arguments only (the head is accounted for, not an escape).
                        if (!_maCallSites.TryGetValue(calleeName, out var list))
                        {
                            list = new List<(string, List<Expr>)>();
                            _maCallSites[calleeName] = list;
                        }

                        list.Add((enclosing, args));
                        foreach (var a in args)
                        {
                            CollectCallsAndEscapes(a, enclosing);
                        }

                        return;
                    }

                    // Not a complete saturated call to a known function: fall through to the generic
                    // walk, which will surface any known-function name as an escape.
                    var call = (Expr.Call)e;
                    CollectCallsAndEscapes(call.Func, enclosing);
                    CollectCallsAndEscapes(call.Arg, enclosing);
                    return;
                }

            case Expr.Var v:
                if (_maFuncs.ContainsKey(v.Name))
                {
                    _maEscaped.Add(v.Name);
                }

                return;

            case Expr.QualifiedVar qv:
                if (ResolveSpecializableCalleeName(qv) is { } qn && _maFuncs.ContainsKey(qn))
                {
                    _maEscaped.Add(qn);
                }

                return;

            case Expr.If i:
                CollectCallsAndEscapes(i.Cond, enclosing);
                CollectCallsAndEscapes(i.Then, enclosing);
                CollectCallsAndEscapes(i.Else, enclosing);
                return;

            case Expr.Match m:
                CollectCallsAndEscapes(m.Value, enclosing);
                foreach (var c in m.Cases)
                {
                    CollectCallsAndEscapes(c.Body, enclosing);
                    if (c.Guard is not null)
                    {
                        CollectCallsAndEscapes(c.Guard, enclosing);
                    }
                }

                return;

            case Expr.Let l:
                WalkBindingValue(l.Name, l.Value, enclosing);
                CollectCallsAndEscapes(l.Body, enclosing);
                return;

            case Expr.LetResult lr:
                WalkBindingValue(lr.Name, lr.Value, enclosing);
                CollectCallsAndEscapes(lr.Body, enclosing);
                return;

            case Expr.LetRec lrec:
                WalkBindingValue(lrec.Name, lrec.Value, enclosing);
                CollectCallsAndEscapes(lrec.Body, enclosing);
                return;

            case Expr.Lambda lam:
                CollectCallsAndEscapes(lam.Body, enclosing);
                return;

            case Expr.Add x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.Subtract x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.Multiply x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.Divide x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.BitwiseAnd x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.BitwiseOr x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.BitwiseXor x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.ShiftLeft x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.ShiftRight x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.BitwiseNot x: CollectCallsAndEscapes(x.Operand, enclosing); return;
            case Expr.GreaterThan x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.LessThan x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.GreaterOrEqual x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.LessOrEqual x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.Equal x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.NotEqual x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.ResultPipe x: CollectBinary(x.Left, x.Right, enclosing); return;
            case Expr.ResultMapErrorPipe x: CollectBinary(x.Left, x.Right, enclosing); return;

            case Expr.Cons cons:
                CollectBinary(cons.Head, cons.Tail, enclosing);
                return;

            case Expr.Await aw:
                CollectCallsAndEscapes(aw.Task, enclosing);
                return;

            case Expr.TupleLit t:
                foreach (var el in t.Elements)
                {
                    CollectCallsAndEscapes(el, enclosing);
                }

                return;

            case Expr.ListLit lst:
                foreach (var el in lst.Elements)
                {
                    CollectCallsAndEscapes(el, enclosing);
                }

                return;

            case Expr.RecordLit rec:
                foreach (var (_, fv) in rec.Fields)
                {
                    CollectCallsAndEscapes(fv, enclosing);
                }

                return;

            case Expr.RecordUpdate ru:
                CollectCallsAndEscapes(ru.Target, enclosing);
                foreach (var (_, uv) in ru.Updates)
                {
                    CollectCallsAndEscapes(uv, enclosing);
                }

                return;

            default:
                return;
        }
    }

    private void CollectBinary(Expr left, Expr right, string enclosing)
    {
        CollectCallsAndEscapes(left, enclosing);
        CollectCallsAndEscapes(right, enclosing);
    }

    /// <summary>
    /// Walks a binding's value. When the value is a registered lambda function <paramref name="name"/>,
    /// its innermost body is walked with the enclosing function switched to <paramref name="name"/>
    /// (so calls inside it resolve their <c>Var</c> arguments against that function's parameters);
    /// otherwise the value is walked under the current enclosing function.
    /// </summary>
    private void WalkBindingValue(string name, Expr value, string enclosing)
    {
        if (!_maAmbiguous.Contains(name) && _maFuncs.TryGetValue(name, out var info))
        {
            CollectCallsAndEscapes(info.Body, name);
        }
        else
        {
            CollectCallsAndEscapes(StripOrSelf(value), enclosing);
        }
    }
}
