using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // A changing, fully normalizable generic variant needs an owned loop root just
    // like a monomorphic variant. Decide before reuse admission: an arena update
    // may otherwise borrow an iteration's RC children and prevent their release.
    // Keep this contextual rather than changing the shared layout predicates.
    private bool IsGeneralRcGenericVariantLoopParameter(
        TcoContext tco,
        TcoParamStaticFacts facts,
        TypeRef parameterType)
    {
        if (!GeneralRcEnabled
            || Environment.GetEnvironmentVariable("GRC_NO_TCO") is not null
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GRC_NO_GENERICVARIANTLOOPRC"))
            || !facts.HasVisibleBinding
            || facts.LoopInvariant
            || tco.TmcShapePresent
            || tco.ParamNames.Any(name => _traitDictionaryParameterMetadata.ContainsKey(name) || _opParamMeta.ContainsKey(name))
            || _linearReuseNames.Contains(facts.ParamName)
            || _linearSpecializationAccumulators.Contains(facts.ParamName)
            || _resetSafeAccumulators.Contains(facts.ParamName)
            || Prune(parameterType) is not TypeRef.TNamedType named
            || named.TypeArgs.Count == 0
            || named.Symbol.Constructors.Count < 2
            || ContainsUnresolvedLayoutType(named, [])
            || !IsGeneralRcAdmissible(named)
            || !IsGeneralRcNamedType(named)
            || tco.OwnershipFunction is not { } function
            || GetOwnershipSummary(function) is not { } summary
            || tco.Body is null
            || !GenericVariantReturnsOwnedRoot(tco.Body, tco, facts.ParamName, new HashSet<string>(StringComparer.Ordinal)))
        {
            return false;
        }

        foreach (TcoParamStructuralFacts structural in summary.TcoParamFacts)
        {
            if (structural.ParameterOrdinal == facts.ParameterOrdinal
                && string.Equals(structural.ParameterName, facts.ParamName, StringComparison.Ordinal))
            {
                return structural.Shape != TcoSelfCallArgumentShape.UnchangedPassthrough;
            }
        }

        return false;
    }

    // This is an exit contract, not another back-edge classifier. Canonical structural
    // facts above prove the tail edges. Refuse aggregate/unknown exits: arena demotion
    // of a generic map can borrow unsupported list fields from the released RC root.
    private bool GenericVariantReturnsOwnedRoot(
        Expr body, TcoContext tco, string parameter, HashSet<string> shadowed)
    {
        switch (body)
        {
            case Expr.Var variable:
                return !shadowed.Contains(parameter)
                    && string.Equals(variable.Name, parameter, StringComparison.Ordinal);
            case Expr.If conditional:
                return GenericVariantReturnsOwnedRoot(conditional.Then, tco, parameter, shadowed)
                    && GenericVariantReturnsOwnedRoot(conditional.Else, tco, parameter, shadowed);
            case Expr.Match match:
                foreach (MatchCase arm in match.Cases)
                {
                    HashSet<string> armShadowed = new HashSet<string>(shadowed, StringComparer.Ordinal);
                    CollectPatternBinders(arm.Pattern, armShadowed);
                    if (!GenericVariantReturnsOwnedRoot(arm.Body, tco, parameter, armShadowed))
                    {
                        return false;
                    }
                }

                return match.Cases.Count > 0;
            case Expr.Let binding:
                HashSet<string> bodyShadowed = new HashSet<string>(shadowed, StringComparer.Ordinal) { binding.Name };
                return GenericVariantReturnsOwnedRoot(binding.Body, tco, parameter, bodyShadowed);
            case Expr.Call:
                List<Expr> arguments = [];
                Expr callee = CollectCallArgs(body, arguments);
                return callee is Expr.Var self
                    && !shadowed.Contains(tco.SelfName)
                    && string.Equals(self.Name, tco.SelfName, StringComparison.Ordinal)
                    && arguments.Count == tco.ParamCount;
            default:
                return false;
        }
    }

}
