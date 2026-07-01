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

    // Result-reachability (may-alias) summary (CO-2 result-alias elision): per registered function, a
    // conservative OVER-APPROXIMATION of which of its own parameters the function's RESULT value may be
    // reachable-through / alias, as a per-parameter multiplicity, plus a "poison" flag meaning the
    // result is not provably confined to the parameters (it may alias a top-level/global binding, an
    // unmodeled value, or be internally shared). Multiplicities are capped at 2: a parameter reachable
    // through two simultaneous heap positions (internal sharing, e.g. `Node(x)(0)(x)`) forces poison,
    // because moving such an argument would leave two live aliases the reuse fold could corrupt.
    // Computed once as a monotone least fixpoint — every function starts with empty reach and not
    // poisoned; reach sets and poison only grow until stable — so an under-computed early pass can only
    // stay smaller, never over-claim (the sound direction for a may-analysis). Used to admit a fold
    // accumulator seeded by a builder's *result* as a move: a `wrap`-style builder
    // (`let wrap x = Node(x)(0)(Leaf)`) reaches {x}, so `wrap(<arg>)` is a move iff the argument bound
    // to `x` is itself a move. A function whose result reaches {} and is not poisoned is result-fresh
    // (its result is a uniquely-owned freshly-allocated value for any arguments) — the higher-order-seed
    // case, subsumed here as the empty-reach special case.
    private readonly Dictionary<string, (Dictionary<string, int> Counts, bool Poison)> _maResultReach =
        new(StringComparer.Ordinal);

    // Reach multiplicity cap: any count reaching this is folded into the poison flag (internal sharing).
    private const int ReachCap = 2;

    // Per-binding synthetic identity token counter (reset at the start of each function's ResultReach
    // pass). Every locally-introduced binding (a `let`/let-result value, a `match` pattern variable) is
    // given a unique synthetic reach token summed into its env reach, so multiplicity is tracked for a
    // *fresh* (non-parameter) heap value exactly as it is for a parameter: embedding the same bound name
    // through two simultaneous heap positions (e.g. `let x = Node(...)(u)(...) in Node(x)(0)(x)`, or
    // `[x, x]`) sums the token to the cap and poisons — the fresh value is internally shared, so not
    // uniquely owned, and its entry copy (which exists precisely to unshare it) must stay. Tokens are
    // stripped from the stored per-function summary (see ComputeResultReach): a count-1 token is a fresh
    // internal cell escaping via a single path (harmless, confined), and a count-2 token has already set
    // poison during the sum. Token keys are prefixed with '#', which no real identifier uses, so they can
    // never collide with a parameter name and never reach IsResultAliasMove (which maps real params only).
    private int _maReachToken;

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
        _maResultReach.Clear();
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
        ComputeResultReach();
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

        // (CO-2 result-alias) A saturated call to a registered function written inline at the call site
        // whose result is a move here: the callee's result-reach summary is not poisoned, and for every
        // parameter its result may alias, the argument bound to it is itself a move (recursively). A
        // result-fresh callee reaches {} and is admitted unconditionally (the empty-reach special case,
        // subsuming the earlier higher-order-seed rule); a `wrap`-style builder that returns a parameter
        // is admitted exactly when that parameter's argument is a move.
        if (IsResultAliasMove(arg, enclosing))
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
                // Fresh by construction, or (CO-2 result-alias) the result of a builder call that is a
                // move here (result-reach not poisoned, and every reached parameter's argument is itself
                // a move) — both give a uniquely-owned, internally-unshared bound value.
                && (IsFullyFreshConstruction(boundRhs) || IsResultAliasMove(boundRhs, enclosing))
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
    /// Computes the result-reachability (may-alias) summary (<see cref="_maResultReach"/>) as a monotone
    /// least fixpoint: every registered function starts with empty reach and not poisoned, and each pass
    /// recomputes its result-reach from its body under the current summaries, unioning the growth in,
    /// until stable. Starting from the empty (bottom) approximation and only growing is the sound
    /// direction for a MAY-analysis (over-approximation): an under-computed early pass can only stay
    /// smaller, never over-claim confinement. Recursion is handled naturally — a self/mutual call reads
    /// the callee's current (growing) summary — so a recursive builder converges without a special cycle
    /// rule; a poison source (a global reference, an unmodeled node, or internal sharing) is detected
    /// directly in the body and propagates through the fixpoint.
    /// </summary>
    private void ComputeResultReach()
    {
        _maResultReach.Clear();
        foreach (var name in _maFuncs.Keys)
        {
            _maResultReach[name] = ReachBottom();
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (name, info) in _maFuncs)
            {
                var env = new Dictionary<string, (Dictionary<string, int> Counts, bool Poison)>(StringComparer.Ordinal);
                foreach (var p in info.Params)
                {
                    env[p] = (new Dictionary<string, int>(StringComparer.Ordinal) { [p] = 1 }, false);
                }

                _maReachToken = 0;
                var computed = StripSyntheticTokens(ResultReach(info.Body, env));
                var merged = ReachJoin(_maResultReach[name], computed);
                if (!ReachEquals(_maResultReach[name], merged))
                {
                    _maResultReach[name] = merged;
                    changed = true;
                }
            }
        }
    }

    private static (Dictionary<string, int> Counts, bool Poison) ReachBottom()
        => (new Dictionary<string, int>(StringComparer.Ordinal), false);

    private static (Dictionary<string, int> Counts, bool Poison) ReachPoisoned()
        => (new Dictionary<string, int>(StringComparer.Ordinal), true);

    // Sequential composition (simultaneously-live heap positions — a constructor's heap fields, an
    // aggregate's elements): multiplicities add, so a parameter reachable through two positions reaches
    // the cap and poisons (internal sharing — a moved argument would be doubly aliased in the result).
    private static (Dictionary<string, int> Counts, bool Poison) ReachSum(
        (Dictionary<string, int> Counts, bool Poison) a,
        (Dictionary<string, int> Counts, bool Poison) b)
    {
        var counts = new Dictionary<string, int>(a.Counts, StringComparer.Ordinal);
        bool poison = a.Poison || b.Poison;
        foreach (var (k, v) in b.Counts)
        {
            int nv = (counts.TryGetValue(k, out var e) ? e : 0) + v;
            counts[k] = nv >= ReachCap ? ReachCap : nv;
        }

        foreach (var v in counts.Values)
        {
            if (v >= ReachCap)
            {
                poison = true;
            }
        }

        return (counts, poison);
    }

    // Branch join (if/match arms — at most one executes): multiplicities take the max, so distinct arms
    // never manufacture sharing.
    private static (Dictionary<string, int> Counts, bool Poison) ReachMax(
        (Dictionary<string, int> Counts, bool Poison) a,
        (Dictionary<string, int> Counts, bool Poison) b)
    {
        var counts = new Dictionary<string, int>(a.Counts, StringComparer.Ordinal);
        foreach (var (k, v) in b.Counts)
        {
            counts[k] = counts.TryGetValue(k, out var e) && e > v ? e : v;
        }

        return (counts, a.Poison || b.Poison);
    }

    // Fixpoint join: identical to the branch max (grow reach sets / poison until stable).
    private static (Dictionary<string, int> Counts, bool Poison) ReachJoin(
        (Dictionary<string, int> Counts, bool Poison) a,
        (Dictionary<string, int> Counts, bool Poison) b)
        => ReachMax(a, b);

    // Scale by a callee's per-parameter multiplicity: a callee embedding a parameter twice doubles the
    // reach of the argument bound to it (again capped into poison at the sharing boundary).
    private static (Dictionary<string, int> Counts, bool Poison) ReachScale(
        (Dictionary<string, int> Counts, bool Poison) a,
        int factor)
    {
        if (factor <= 0)
        {
            return ReachBottom();
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        bool poison = a.Poison;
        foreach (var (k, v) in a.Counts)
        {
            int nv = v * factor;
            if (nv >= ReachCap)
            {
                nv = ReachCap;
                poison = true;
            }

            counts[k] = nv;
        }

        return (counts, poison);
    }

    private static bool ReachEquals(
        (Dictionary<string, int> Counts, bool Poison) a,
        (Dictionary<string, int> Counts, bool Poison) b)
    {
        if (a.Poison != b.Poison || a.Counts.Count != b.Counts.Count)
        {
            return false;
        }

        foreach (var (k, v) in a.Counts)
        {
            if (!b.Counts.TryGetValue(k, out var bv) || bv != v)
            {
                return false;
            }
        }

        return true;
    }

    // A fresh per-binding synthetic identity token, reach {token:1}. Summed into a binding's env reach so
    // multiplicity of a fresh (non-parameter) heap value is tracked exactly as for a parameter.
    private (Dictionary<string, int> Counts, bool Poison) TokenReach()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["#" + _maReachToken] = 1 };
        _maReachToken++;
        return (counts, false);
    }

    // Drops synthetic '#'-prefixed identity tokens from a summary (they are local to one function's
    // ResultReach pass and must never be stored or reach IsResultAliasMove). Poison is preserved: a
    // token that reached the cap already set poison during the sum before it is stripped here.
    private static (Dictionary<string, int> Counts, bool Poison) StripSyntheticTokens(
        (Dictionary<string, int> Counts, bool Poison) r)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (k, v) in r.Counts)
        {
            if (k.Length == 0 || k[0] != '#')
            {
                counts[k] = v;
            }
        }

        return (counts, r.Poison);
    }

    /// <summary>
    /// The result-reach of <paramref name="e"/> as the returned value of a function body: the set of the
    /// enclosing function's parameters (with multiplicity) the value may alias, plus a poison flag when
    /// the value is not provably confined to those parameters. <paramref name="env"/> maps each in-scope
    /// name to its reach (each parameter to itself; let/pattern bindings to the reach of what they bind).
    /// A bare free reference (a top-level/global binding or unmodeled name), a non-sole nullary or
    /// partially-applied constructor, an unresolved/under-or-over-applied call, or any unmodeled node
    /// poisons; the conservative default is poison, so an unproven shape never over-claims confinement.
    /// </summary>
    private (Dictionary<string, int> Counts, bool Poison) ResultReach(
        Expr e,
        Dictionary<string, (Dictionary<string, int> Counts, bool Poison)> env)
    {
        switch (e)
        {
            case Expr.IntLit:
            case Expr.UIntLit:
            case Expr.FloatLit:
            case Expr.StrLit:
            case Expr.BoolLit:
            // Arithmetic/comparison/bitwise/shift results are copy-typed scalars — they reach no heap
            // cell, so they are confined and reach no parameter.
            case Expr.Add:
            case Expr.Subtract:
            case Expr.Multiply:
            case Expr.Divide:
            case Expr.BitwiseAnd:
            case Expr.BitwiseOr:
            case Expr.BitwiseXor:
            case Expr.ShiftLeft:
            case Expr.ShiftRight:
            case Expr.BitwiseNot:
            case Expr.GreaterThan:
            case Expr.LessThan:
            case Expr.GreaterOrEqual:
            case Expr.LessOrEqual:
            case Expr.Equal:
            case Expr.NotEqual:
                return ReachBottom();

            case Expr.Var v:
                if (env.TryGetValue(v.Name, out var bound))
                {
                    return bound;
                }

                if (_constructorSymbols.TryGetValue(v.Name, out var ctor))
                {
                    // A nullary constructor value reaches no parameter; it is confined only when it is
                    // the sole nullary constructor of its type (a no-op-safe tag cell). Any other nullary
                    // (a possibly-shared non-sole singleton) or a partially-applied constructor poisons.
                    return ctor.Arity == 0 && IsSoleNullaryConstructor(ctor) ? ReachBottom() : ReachPoisoned();
                }

                // A free reference: a top-level/global binding or an unmodeled name — not confined.
                return ReachPoisoned();

            case Expr.QualifiedVar:
                return ReachPoisoned();

            // Control flow: the returned value is one of the arms; the scrutinee/condition is not part
            // of the returned value (but a match's scrutinee reach flows into its pattern bindings).
            case Expr.If i:
                return ReachMax(ResultReach(i.Then, env), ResultReach(i.Else, env));
            case Expr.Match m:
                return MatchReach(m, env);

            case Expr.Let l:
                return ResultReach(l.Body, ExtendEnv(env, l.Name, ReachSum(ResultReach(l.Value, env), TokenReach())));
            case Expr.LetResult lr:
                return ResultReach(lr.Body, ExtendEnv(env, lr.Name, ReachSum(ResultReach(lr.Value, env), TokenReach())));
            case Expr.LetRec lrec:
                // A self-referential local binding is not modeled; treat any use of it as poison.
                return ResultReach(lrec.Body, ExtendEnv(env, lrec.Name, ReachPoisoned()));

            // Aggregate/list literals: every element is simultaneously live, so reach sums.
            case Expr.Cons cons:
                return ReachSum(ResultReach(cons.Head, env), ResultReach(cons.Tail, env));
            case Expr.ListLit lst:
                return SumReach(lst.Elements, env);
            case Expr.TupleLit t:
                return SumReach(t.Elements, env);
            case Expr.RecordLit rec:
                {
                    var acc = ReachBottom();
                    foreach (var (_, fv) in rec.Fields)
                    {
                        acc = ReachSum(acc, ResultReach(fv, env));
                    }

                    return acc;
                }

            case Expr.Call:
                return CallReach(e, env);

            default:
                // Unmodeled node (Lambda, Await, RecordUpdate, Result pipes, …): not provably confined.
                return ReachPoisoned();
        }
    }

    private (Dictionary<string, int> Counts, bool Poison) SumReach(
        IReadOnlyList<Expr> exprs,
        Dictionary<string, (Dictionary<string, int> Counts, bool Poison)> env)
    {
        var acc = ReachBottom();
        foreach (var el in exprs)
        {
            acc = ReachSum(acc, ResultReach(el, env));
        }

        return acc;
    }

    private static Dictionary<string, (Dictionary<string, int> Counts, bool Poison)> ExtendEnv(
        Dictionary<string, (Dictionary<string, int> Counts, bool Poison)> env,
        string name,
        (Dictionary<string, int> Counts, bool Poison) value)
    {
        var env2 = new Dictionary<string, (Dictionary<string, int> Counts, bool Poison)>(env, StringComparer.Ordinal)
        {
            [name] = value,
        };
        return env2;
    }

    /// <summary>
    /// Result-reach of a <c>match</c>: the scrutinee's reach flows into each arm's pattern variables
    /// (each pattern binding may alias the scrutinee — sub-values of a value the fold could overwrite),
    /// then the arms join by max (at most one executes). A copy-typed pattern variable so bound is only
    /// ever used in a copy position (ignored by the constructor case), so over-approximating it here is
    /// harmless.
    /// </summary>
    private (Dictionary<string, int> Counts, bool Poison) MatchReach(
        Expr.Match m,
        Dictionary<string, (Dictionary<string, int> Counts, bool Poison)> env)
    {
        var scrut = ResultReach(m.Value, env);
        (Dictionary<string, int> Counts, bool Poison)? acc = null;
        foreach (var c in m.Cases)
        {
            var env2 = BindPatternReach(c.Pattern, scrut, env);
            var arm = ResultReach(c.Body, env2);
            acc = acc is null ? arm : ReachMax(acc.Value, arm);
        }

        return acc ?? ReachPoisoned();
    }

    private Dictionary<string, (Dictionary<string, int> Counts, bool Poison)> BindPatternReach(
        Pattern p,
        (Dictionary<string, int> Counts, bool Poison) scrut,
        Dictionary<string, (Dictionary<string, int> Counts, bool Poison)> env)
    {
        var names = new List<string>();
        CollectPatternVars(p, names);
        if (names.Count == 0)
        {
            return env;
        }

        var env2 = new Dictionary<string, (Dictionary<string, int> Counts, bool Poison)>(env, StringComparer.Ordinal);
        foreach (var n in names)
        {
            // Each pattern variable may alias the scrutinee (so it carries the scrutinee's reach) but is a
            // distinct sub-value, so it also gets its own fresh identity token: two DIFFERENT pattern vars
            // in one heap shape (`Node(l)(0)(r)`) stay disjoint, while the SAME pattern var used twice
            // (`Node(l)(0)(l)`) sums its token to the cap and poisons — even when the scrutinee is fresh.
            env2[n] = ReachSum(scrut, TokenReach());
        }

        return env2;
    }

    private static void CollectPatternVars(Pattern p, List<string> into)
    {
        switch (p)
        {
            case Pattern.Var v:
                into.Add(v.Name);
                break;
            case Pattern.Constructor c:
                foreach (var sub in c.Patterns)
                {
                    CollectPatternVars(sub, into);
                }

                break;
            case Pattern.Tuple t:
                foreach (var sub in t.Elements)
                {
                    CollectPatternVars(sub, into);
                }

                break;
            case Pattern.Cons cons:
                CollectPatternVars(cons.Head, into);
                CollectPatternVars(cons.Tail, into);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Result-reach of a call expression: a saturated data-constructor application (its heap-typed fields
    /// sum — copy-typed fields hold scalars inline and are ignored), or a saturated call to a registered
    /// function (substitute the argument bound to each parameter the callee's result may reach, scaled by
    /// its multiplicity, and sum). A partial/over-applied constructor, an unresolved/ambiguous/mis-arity
    /// call, or a poisoned callee poisons.
    /// </summary>
    private (Dictionary<string, int> Counts, bool Poison) CallReach(
        Expr e,
        Dictionary<string, (Dictionary<string, int> Counts, bool Poison)> env)
    {
        var args = new List<Expr>();
        var head = CollectCallArgs(e, args);

        if (head is Expr.Var hv && _constructorSymbols.TryGetValue(hv.Name, out var ctor))
        {
            if (args.Count != ctor.Arity)
            {
                return ReachPoisoned(); // partial or over-applied constructor
            }

            var acc = ReachBottom();
            for (int i = 0; i < args.Count; i++)
            {
                bool copyField = ctor.ParameterTypes.Count == ctor.Arity
                    && BuiltinRegistry.IsCopyType(ctor.ParameterTypes[i]);
                if (copyField)
                {
                    continue; // inline scalar — cannot alias a heap cell the fold overwrites
                }

                acc = ReachSum(acc, ResultReach(args[i], env));
            }

            return acc;
        }

        string? name = head switch
        {
            Expr.Var v => v.Name,
            Expr.QualifiedVar => ResolveSpecializableCalleeName(head),
            _ => null,
        };

        if (name is null
            || _maAmbiguous.Contains(name)
            || !_maFuncs.TryGetValue(name, out var info)
            || args.Count != info.Params.Count
            || !_maResultReach.TryGetValue(name, out var summary)
            || summary.Poison)
        {
            return ReachPoisoned();
        }

        var result = ReachBottom();
        foreach (var (paramName, mult) in summary.Counts)
        {
            int idx = info.Params.IndexOf(paramName);
            if (idx < 0 || idx >= args.Count)
            {
                return ReachPoisoned();
            }

            result = ReachSum(result, ReachScale(ResultReach(args[idx], env), mult));
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="arg"/> is a saturated call to a registered function whose result is a
    /// MOVE at this site: the callee's result-reach summary is not poisoned, and for every parameter its
    /// result may alias, the argument bound to it here is itself a move (recursively via
    /// <see cref="ArgIsMove"/>). A result-fresh callee reaches {} and is admitted unconditionally (the
    /// empty-reach special case — subsuming the earlier higher-order-seed rule); a <c>wrap</c>-style
    /// builder that returns/embeds a parameter is admitted exactly when that parameter's argument is a
    /// move. A saturated constructor application is excluded (already covered by
    /// <see cref="IsFullyFreshConstruction"/>); this is the non-constructor case that rule cannot see
    /// through.
    /// </summary>
    private bool IsResultAliasMove(Expr arg, string enclosing)
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

        if (name is null
            || _maAmbiguous.Contains(name)
            || !_maFuncs.TryGetValue(name, out var info)
            || args.Count != info.Params.Count
            || !_maResultReach.TryGetValue(name, out var summary)
            || summary.Poison)
        {
            return false;
        }

        foreach (var (paramName, _) in summary.Counts)
        {
            int idx = info.Params.IndexOf(paramName);
            if (idx < 0 || idx >= args.Count || !ArgIsMove(args[idx], enclosing))
            {
                return false;
            }
        }

        return true;
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
