using System.Diagnostics.CodeAnalysis;
using Ashes.Frontend;

namespace Ashes.Semantics;

// Result-provenance classification for FunctionOwnershipSummary (Perceus-unification Phase 0, see
// docs/md/future/PERCEUS_UNIFICATION.md §5 item 3). This generalizes and strictly subsumes what
// Lowering.cs's IR-level backward-scan mechanism (_functionReturnedClosureLabels /
// _runtimeManagedFunctionResultLabels, populated by RecordFunctionResultProvenance /
// RecordReturnedClosureLabel) tracks: that mechanism only recognizes a returned closure when a
// function's body temp was produced by a literal MakeClosure/MakeClosureStack instruction found by
// scanning backward through already-emitted IR for the CURRENT function only — it has no way to see a
// result computed by CALLING another named function (a sibling helper), because a Call instruction
// does not look like a closure-producing instruction even though its callee, at runtime, returns one.
//
// This pass instead classifies each registered function's innermost (fully curried, per _maFuncs)
// body once, at the AST level, before lowering. A body that is itself a call to another registered
// function is resolved transitively (memoized, cycle-guarded) through as many sibling-helper hops as
// the program actually has — not just one. Phase 0 only computes and shadow-compares this; nothing yet
// consults it for an actual lowering decision.
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
    /// If/Match/Let/LetResult/LetRecursive exactly as <see cref="CollectFreshAdtEscapeArms"/> already
    /// does for the CO-38-fixed <c>ProducesFreshRuntimeManageableAdt</c> — rather than inspecting only
    /// the body's outermost node. This matters concretely: the idiomatic shape for a recursive function
    /// in this language (fannkuch's <c>nextPerm</c>/<c>loop</c> included) is exactly "match on my own
    /// accumulator, then branch to either a fresh construction or a forwarding call to a sibling
    /// helper" — a single-node inspection would default nearly every real recursive function to
    /// conservative-false and silently fail to classify the case this field exists for.
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

        var arms = new List<Expr>();
        CollectFreshAdtEscapeArms(info.Body, arms);

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
            var armProvenance = ClassifyExpressionProvenance(arm);
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
    private FunctionResultProvenance ClassifyExpressionProvenance(Expr expression)
    {
        if (IsDirectRcConstruction(expression)
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

    private bool IsDirectRcConstruction(Expr body)
    {
        if (body is Expr.Cons or Expr.ListLit or Expr.TupleLit or Expr.RecordLit)
        {
            return true;
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
            && arguments.Count == constructor.Arity;
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
