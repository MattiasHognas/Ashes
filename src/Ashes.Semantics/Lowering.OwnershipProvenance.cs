using System.Diagnostics.CodeAnalysis;
using Ashes.Frontend;

namespace Ashes.Semantics;

// Result-provenance classification for FunctionOwnershipSummary. This generalizes and strictly
// subsumes what Lowering.cs's old IR-level backward-scan mechanism (_functionReturnedClosureLabels /
// _runtimeManagedFunctionResultLabels, populated by RecordFunctionResultProvenance /
// RecordReturnedClosureLabel) used to track: that mechanism only recognized a returned closure when a
// function's body temp was produced by a literal MakeClosure/MakeClosureStack instruction found by
// scanning backward through already-emitted IR for the CURRENT function only — it had no way to see a
// result computed by CALLING another named function (a sibling helper), because a Call instruction
// does not look like a closure-producing instruction even though its callee, at runtime, returns one.
//
// This pass instead classifies each registered function's innermost (fully curried, per _maFuncs)
// body once, at the AST level, before lowering. A body that is itself a call to another registered
// function is resolved transitively (memoized, cycle-guarded) through as many sibling-helper hops as
// the program actually has — not just one. Phase 3 wired this directly into the real decision path
// (TryResolveKnownFunctionResultOwnership/IsDirectRuntimeManagedFunctionCall in Lowering.cs) and
// retired _runtimeManagedFunctionResultLabels/RecordFunctionResultProvenance entirely;
// _functionReturnedClosureLabels/RecordReturnedClosureLabel remain, but now serve only
// IsKnownRuntimeNormalizedFunctionArgument's unrelated TCO-argument-normalization question.
public sealed partial class Lowering
{
    private readonly Dictionary<string, FunctionResultProvenance> _maProvenanceMemo =
        new(StringComparer.Ordinal);

    private readonly HashSet<string> _maProvenanceInProgress = new(StringComparer.Ordinal);

    /// <summary>
    /// The result provenance of registered function <paramref name="function"/>, resolved transitively
    /// through any chain of sibling-helper forwarding. A cycle (mutual or self forwarding through the
    /// function's own bare body, an unusual shape) resolves to the conservative default
    /// (not RC-eligible, no forward target) — the sound under-approximation, matching this file's own
    /// "default is always conservative" convention (see Lowering.MoveAnalysis.cs's header comment).
    /// </summary>
    private FunctionResultProvenance ResolveFunctionResultProvenance(string function)
    {
        if (_maProvenanceMemo.TryGetValue(function, out var cached))
        {
            return cached;
        }

        if (!_maProvenanceInProgress.Add(function))
        {
            return new FunctionResultProvenance(false, null);
        }

        var result = ClassifyFunctionResultProvenance(function);
        _maProvenanceInProgress.Remove(function);
        _maProvenanceMemo[function] = result;
        return result;
    }

    /// <summary>
    /// Classifies a registered function's result by its TERMINAL arms — recursing through
    /// If/Match/Let/LetResult/LetRecursive, additionally substituting a terminal bare variable reference
    /// through any local let-binding that produced it (see <see cref="CollectResultProvenanceTerminalArms"/>)
    /// — rather than inspecting only the body's outermost node. This matters concretely: the idiomatic
    /// shape for a recursive function in this language (fannkuch's <c>nextPerm</c>/<c>loop</c> included)
    /// is exactly "match on my own accumulator, then branch to either a fresh construction or a
    /// forwarding call to a sibling helper" — a single-node inspection would default nearly every real
    /// recursive function to conservative-false and silently fail to classify the case this field exists
    /// for. Likewise, the equally idiomatic "let result = ‹fresh construction› in result" shape (binding
    /// the result to a name before returning it, extremely common style) would default to
    /// conservative-false without the let-substitution, since the terminal arm is then a bare variable
    /// reference, not the construction itself.
    ///
    /// A terminal arm that recurses into <paramref name="function"/> itself never conflicts (the
    /// CO-38 precedent: "a recursive call... never conflicts") — by induction, its eventual value IS
    /// whatever this very classification concludes, so it neither confirms nor denies eligibility and
    /// is excluded from the agreement requirement below. Every OTHER terminal arm must independently be
    /// RC-eligible for the function as a whole to be RC-eligible (mixing an eligible arm with an
    /// ineligible sibling is exactly the CO-38/PR #299 hazard — silently mixed representations across
    /// arms of the same function). If every non-self-recursive forwarding arm agrees on the same single
    /// target function, that target is reported; disagreement (or no forwarding arm at all) reports no
    /// single hop, even when every arm is independently RC-eligible some other way.
    /// </summary>
    private FunctionResultProvenance ClassifyFunctionResultProvenance(string function)
    {
        if (!_maFuncs.TryGetValue(function, out var info))
        {
            return new FunctionResultProvenance(false, null);
        }

        // A function whose innermost body is DIRECTLY (with zero unwrapping — not through a let, if, or
        // match) a bare string literal is unconditionally normalized to a fresh RC string by
        // LowerEscapingResult's own top-level normalizeStaticString check (LowerLambdaCoreLowerBody
        // always passes normalizeStaticString: true for a function's own innermost body). This is a much
        // narrower guarantee than "any string literal terminal arm" — a literal reached through a let
        // binding, or as one arm of an if/match, is NOT unconditionally normalized this way (a match arm
        // only normalizes when a SIBLING arm is a genuine fresh producer; an if-arm never does) — so this
        // check must run on info.Body directly, before any let-substitution/arm-collection unwraps it.
        if (info.Body is Expr.StrLit)
        {
            return new FunctionResultProvenance(true, null);
        }

        var letBindings = new Dictionary<string, Expr>(StringComparer.Ordinal);
        var arms = new List<Expr>();
        CollectResultProvenanceTerminalArms(info.Body, arms, letBindings);
        return ClassifyFunctionResultProvenanceFromArms(function, arms, letBindings);
    }

    private FunctionResultProvenance ClassifyFunctionResultProvenanceFromArms(
        string function,
        List<Expr> arms,
        Dictionary<string, Expr> letBindings)
    {
        bool allEligible = true;
        bool sawForward = false;
        bool forwardAmbiguous = false;
        string? commonForward = null;
        int consideredArms = 0;

        foreach (Expr arm in arms)
        {
            if (IsSelfRecursiveArm(arm, function))
            {
                continue;
            }

            consideredArms++;
            var armProvenance = ClassifyExpressionProvenance(arm, letBindings);
            if (!armProvenance.RcEligible)
            {
                allEligible = false;
            }

            if (armProvenance.ForwardsTo is { } armTarget)
            {
                if (!sawForward)
                {
                    commonForward = armTarget;
                    sawForward = true;
                }
                else if (!string.Equals(commonForward, armTarget, StringComparison.Ordinal))
                {
                    forwardAmbiguous = true;
                }
            }
        }

        // No non-self-recursive terminal arm at all (every arm recurses into this very function, so
        // there is no base case to found eligibility on): conservative default, not a vacuous true.
        if (consideredArms == 0)
        {
            return new FunctionResultProvenance(false, null);
        }

        return new FunctionResultProvenance(allEligible, forwardAmbiguous ? null : commonForward);
    }

    /// <summary>
    /// Collects the terminal (non-control-flow) arms of <paramref name="body"/>: recurses through
    /// If/Match/Let/LetResult (accumulating each let's bound name and value into
    /// <paramref name="letBindings"/> as it descends) and LetRecursive (body only — a let-recursive
    /// binding is its own separate recursive definition, not a plain forwarding alias, so its value is
    /// never substituted through). A terminal bare variable reference is, in turn, substituted through
    /// <paramref name="letBindings"/> when it names a local let binding already seen on the way down —
    /// this is what lets "let result = ‹fresh construction› in result" classify by the construction, not
    /// by the intervening variable name. <paramref name="letBindings"/> is shared and mutated across the
    /// whole walk (including across If/Match sibling branches) rather than scoped per-branch: safe
    /// because Model-A sequential scoping guarantees a name is always (re)bound by an enclosing let
    /// before any well-typed reference to it, so a stale entry from an already-processed sibling can
    /// never be read by a later sibling that does not itself rebind the name. A plain (non-recursive)
    /// let's value can never reference its own bound name (self-recursion requires <c>let recursive</c>),
    /// so this substitution cannot cycle.
    /// </summary>
    private void CollectResultProvenanceTerminalArms(Expr body, List<Expr> arms, Dictionary<string, Expr> letBindings)
    {
        switch (body)
        {
            case Expr.If iff:
                CollectResultProvenanceTerminalArms(iff.Then, arms, letBindings);
                CollectResultProvenanceTerminalArms(iff.Else, arms, letBindings);
                break;
            case Expr.Match match:
                foreach (MatchCase matchCase in match.Cases)
                {
                    CollectResultProvenanceTerminalArms(matchCase.Body, arms, letBindings);
                }

                break;
            case Expr.Let let:
                letBindings[let.Name] = let.Value;
                CollectResultProvenanceTerminalArms(let.Body, arms, letBindings);
                break;
            case Expr.LetResult letResult:
                letBindings[letResult.Name] = letResult.Value;
                CollectResultProvenanceTerminalArms(letResult.Body, arms, letBindings);
                break;
            case Expr.LetRecursive letRecursive:
                CollectResultProvenanceTerminalArms(letRecursive.Body, arms, letBindings);
                break;
            case Expr.Var v when letBindings.TryGetValue(v.Name, out Expr? boundValue):
                CollectResultProvenanceTerminalArms(boundValue, arms, letBindings);
                break;
            default:
                arms.Add(body);
                break;
        }
    }

    /// <summary>
    /// Classifies one terminal arm expression in isolation: a saturated data-constructor application or
    /// aggregate literal is direct RC-eligible construction; a fully applied call to a builtin declared
    /// fresh-RC-producing (<see cref="BuiltinRegistry.FreshRcResultKind"/>, e.g. <c>Ashes.Text.fromInt</c>)
    /// is likewise direct RC-eligible construction — a call INTO the builtin is itself the fresh-result-
    /// producing expression, exactly like a constructor application; an <see cref="Expr.Add"/> node is
    /// also treated as fresh construction, matching <c>IsRuntimeRcStringProducer</c>'s own unconditional
    /// treatment of it (string `+` always allocates a fresh concatenated result — when the node's
    /// resolved type turns out to be a copy type like Int/Float instead, marking it RC-eligible here is
    /// inert, since only heap-shaped results ever consult this fact downstream); a call to another
    /// registered function is forwarding, resolved transitively; anything else (a bare parameter
    /// passthrough, a call to an unregistered/foreign function, an unmodeled node) is the conservative
    /// default.
    /// </summary>
    private FunctionResultProvenance ClassifyExpressionProvenance(
        Expr expression,
        Dictionary<string, Expr> letBindings)
    {
        if (IsDirectRcConstruction(expression, letBindings)
            || IsRuntimeRcFreshBuiltinProducer(expression)
            || expression is Expr.Add)
        {
            return new FunctionResultProvenance(true, null);
        }

        if (TryResolveForwardTarget(expression, out string? target))
        {
            var targetProvenance = ResolveFunctionResultProvenance(target);
            return new FunctionResultProvenance(targetProvenance.RcEligible, target);
        }

        return new FunctionResultProvenance(false, null);
    }

    private static bool IsSelfRecursiveArm(Expr arm, string function)
    {
        if (arm is not Expr.Call)
        {
            return false;
        }

        var arguments = new List<Expr>();
        Expr head = CollectCallArgs(arm, arguments);
        return head is Expr.Var variable && string.Equals(variable.Name, function, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="body"/> is a heap construction PROVABLY independent of any existing,
    /// possibly-still-aliased value — not merely a node of the right AST shape. <c>Expr.Cons</c>/
    /// <c>Expr.ListLit</c> delegate to the existing <see cref="IsFreshListConstructionExpression"/>
    /// (which itself requires the TAIL chain to bottom out at a literal <c>[]</c>/<c>[...]</c>, never a
    /// bare variable) rather than accepting any Cons node — a naive "Cons is always fresh" check misses
    /// exactly the <c>match xs with | h :: rest -> v :: rest</c> shape (rebuilding a list from an
    /// EXISTING list's tail, a genuine alias, not a fresh sub-structure): this was caught by
    /// Tco_adt_with_shared_tail_list_children_survives_across_iterations producing a corrupted value
    /// (a wrong number, not a crash) when this method still accepted any Cons/TupleLit/RecordLit/
    /// constructor-call node regardless of its children. A constructor application, tuple, or record
    /// literal is fresh only when EVERY argument/field is itself independently fresh
    /// (<see cref="IsFreshConstructionArgument"/>) — the same reasoning applied one level down, since a
    /// tuple/record/ADT cell that merely POINTS AT an aliased heap value is exactly as unsound to treat
    /// as wholly, independently owned. <paramref name="letBindings"/> is threaded through to
    /// <see cref="IsFreshConstructionArgument"/> so a terminal bare-Var argument can still resolve
    /// through a local let binding (see <see cref="CollectResultProvenanceTerminalArms"/>'s own doc).
    ///
    /// Deliberately does NOT recognize a bare <see cref="Expr.Lambda"/> as fresh construction, even
    /// though evaluating one always allocates a new closure object: WHETHER that closure ends up RC- or
    /// arena-represented depends on IsRuntimeRcCopyClosureProducer/ClosureCapturesRuntimeManagedHeapValue,
    /// which call LookupOwnedValue — live, per-scope lowering-time ownership state that does not exist
    /// during this pre-lowering, whole-program AST pass. Treating a Lambda as unconditionally fresh here
    /// would let a function whose closure is ACTUALLY arena-allocated (e.g. captures only copy-type
    /// values) be wrongly reported RC-eligible — a false positive, exactly the dangerous direction this
    /// phase must not risk. This is a permanent scope limit, not a gap to close later: closures are out
    /// of reach for a pre-lowering classifier by construction. A function whose result is a returned
    /// closure therefore always falls to the conservative default here, which in turn means
    /// TryResolveKnownFunctionResultOwnership never resolves such a call — verified this still safely
    /// preserves the untouched, pre-existing dynamic fallback (see
    /// IsConcretelyRuntimeManageableResultType's own TFun case for why that fallback is what actually
    /// matters for a closure result today).
    /// </summary>
    private bool IsDirectRcConstruction(Expr body, Dictionary<string, Expr> letBindings)
    {
        if (body is Expr.Cons or Expr.ListLit)
        {
            return IsFreshListConstructionExpression(body);
        }

        if (body is Expr.TupleLit tupleLit)
        {
            return tupleLit.Elements.All(element => IsFreshConstructionArgument(element, letBindings));
        }

        if (body is Expr.RecordLit recordLit)
        {
            return recordLit.Fields.All(field => IsFreshConstructionArgument(field.Value, letBindings));
        }

        // A bare nullary constructor reference (e.g. `Empty`) is an Expr.Var, not an Expr.Call — mirror
        // ResultReachVar's own treatment: confined (and so RC-eligible) only when it is the SOLE nullary
        // constructor of its type, matching the sound "no-op-safe tag cell" reasoning used throughout
        // this file (a non-sole nullary may be a shared static singleton, not a fresh allocation).
        if (body is Expr.Var nullary
            && _constructorSymbols.TryGetValue(nullary.Name, out ConstructorSymbol? nullaryConstructor)
            && nullaryConstructor is not null
            && nullaryConstructor.Arity == 0)
        {
            return IsSoleNullaryConstructor(nullaryConstructor);
        }

        if (body is not Expr.Call)
        {
            return false;
        }

        var arguments = new List<Expr>();
        Expr head = CollectCallArgs(body, arguments);
        return head is Expr.Var variable
            && _constructorSymbols.TryGetValue(variable.Name, out ConstructorSymbol? constructor)
            && constructor is not null
            && arguments.Count == constructor.Arity
            && arguments.All(argument => IsFreshConstructionArgument(argument, letBindings));
    }

    /// <summary>
    /// Whether a single constructor-application argument or tuple/record field is provably independent
    /// of any existing value — a literal scalar, a nested fresh construction
    /// (<see cref="IsDirectRcConstruction"/>), a fresh-RC-producing builtin call, or an <c>Expr.Add</c>.
    ///
    /// Deliberately does NOT treat a reference to a parameter this function CONSUMES as automatically
    /// fresh, even though that was tried first (to keep <c>helper x = Full(x)</c> RC-eligible, matching
    /// Body_that_calls_a_sibling_helper_forwards_and_inherits_its_eligibility — a pre-existing Phase 0
    /// test): CONSUMED (ownership transferred to the callee) is not the same claim as FRESH/RC-COMPATIBLE
    /// — a consumed parameter can itself be an ARENA-scoped value moved in from the caller, and folding
    /// an arena pointer into a construction this classifier then reports RC-eligible lets the caller's
    /// own arena-reclaim-without-copy path (see TryResolveKnownFunctionResultOwnership's own doc)
    /// silently invalidate that field the next time the arena resets — reading it back afterward returns
    /// garbage/zeroed memory, not a crash. This was caught empirically by
    /// reuse_result_alias_move_elision.ash: `wrap v x = Node(x)(v)(Leaf)` treating consumed `x` as fresh
    /// made `wrap`'s result RC-eligible, and every subsequent read of the wrapped Tree came back `0`
    /// (`nested`/`keep`/`bumped` all printed `0 0 0` instead of `15 7 207`) — exactly the false-positive
    /// direction this phase must never risk, worse than the missed optimization (helper/Full test now
    /// conservatively fails again) accepting this conservative default costs. A bare variable of ANY
    /// origin (a consumed parameter, a pattern-matched sub-structure, a plain local) or an arbitrary
    /// (non-constructor, non-builtin) call is therefore excluded uniformly — any of them could alias a
    /// heap value still owned/reachable (or arena-scoped) elsewhere. This is conservative in the safe
    /// direction: a genuinely fresh forwarding-call argument is missed (falls to false), never the
    /// reverse.
    /// </summary>
    private bool IsFreshConstructionArgument(Expr argument, Dictionary<string, Expr> letBindings)
    {
        return argument switch
        {
            Expr.IntLit or Expr.UIntLit or Expr.FloatLit or Expr.BoolLit or Expr.BigIntLit or Expr.StrLit => true,
            Expr.Add => true,
            _ => IsDirectRcConstruction(argument, letBindings) || IsRuntimeRcFreshBuiltinProducer(argument),
        };
    }

    private bool TryResolveForwardTarget(Expr body, [NotNullWhen(true)] out string? target)
    {
        target = null;
        if (body is not Expr.Call)
        {
            return false;
        }

        var arguments = new List<Expr>();
        Expr head = CollectCallArgs(body, arguments);

        // A saturated constructor application is direct construction, not forwarding — already handled
        // by IsDirectRcConstruction; guard again so a call site is never double-classified.
        if (head is Expr.Var constructorHead && _constructorSymbols.ContainsKey(constructorHead.Name))
        {
            return false;
        }

        string? name = head switch
        {
            Expr.Var v => v.Name,
            Expr.QualifiedVar => ResolveSpecializableCalleeName(head),
            _ => null,
        };

        if (name is null || _maAmbiguous.Contains(name) || !_maFuncs.ContainsKey(name))
        {
            return false;
        }

        target = name;
        return true;
    }
}
