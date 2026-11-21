using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // A method invocation at depth zero runs while the binding is initialized. Deeper
    // invocations need a dictionary only after the corresponding source applications.
    private sealed record TraitDemand(int Depth, TraitConstraint Constraint);

    private bool _immediateEvidenceError;
    private IrProgram? _failedImmediateEvidenceProgram;
    private readonly List<TraitDemand> _traitDemands = [];
    private readonly Dictionary<TypeScheme, IReadOnlyList<TraitDemand>> _schemeDemands =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TypeRef, IReadOnlyList<TraitDemand>> _instantiatedDemands =
        new(ReferenceEqualityComparer.Instance);

    private void RequireMethodEvidence(TraitConstraint constraint, int remaining = 0)
    {
        RequireTraitConstraint(constraint);
        NoteMethodDemand(constraint, remaining);
    }

    private void NoteMethodDemand(TraitConstraint constraint, int remaining = 0)
    {
        if (_collectInferredTraitElaboration && !_suppressTraitConstraintCollection &&
            constraint.TypeArgs.Any(ValueTypeRemainsAbstract))
        {
            _traitDemands.Add(new TraitDemand(_lambdaDepth + remaining, constraint));
        }
    }

    private void NoteCallDemands(TypeRef callee, int arity)
    {
        if (_collectInferredTraitElaboration &&
            _instantiatedDemands.TryGetValue(Prune(callee), out IReadOnlyList<TraitDemand>? demands))
        {
            foreach (TraitDemand demand in demands)
            {
                NoteMethodDemand(demand.Constraint, Math.Max(0, demand.Depth - arity));
            }
        }
    }

    private void InstantiateDemands(TypeScheme scheme, TypeRef type, IReadOnlyDictionary<int, TypeRef> substitution)
    {
        if (_collectInferredTraitElaboration) InstantiateSelectedDemands(scheme, type, substitution);
        if (_collectInferredTraitElaboration && _schemeCallbackSources.TryGetValue(scheme, out Expr? source))
        {
            _instantiatedCallbackSources[type] = source;
        }
        if (_collectInferredTraitElaboration &&
            _schemeDemands.TryGetValue(scheme, out IReadOnlyList<TraitDemand>? demands))
        {
            _instantiatedDemands[type] = demands.Select(demand => demand with
            {
                Constraint = TraitTypeOperations.Substitute(demand.Constraint, substitution),
            }).ToArray();
        }
    }

    private TypeScheme FinishBindingDemands(Expr.Let binding, TypeRef type, TypeScheme scheme, int first)
    {
        if (!_collectInferredTraitElaboration) return scheme;
        RegisterCallbackSource(binding.Value, type, scheme);
        RegisterSelectedDemands(binding.Value, type, scheme);
        if (scheme.Constraints.Count == 0) return scheme;

        // A plain alias has the same latent uses as the function it stores.
        if (binding.Value is Expr.Var or Expr.QualifiedVar)
        {
            NoteCallDemands(type, 0);
        }

        List<TraitDemand> summary = [];
        HashSet<int> held = [];
        List<TraitConstraint> retained = [];
        foreach (TraitConstraint constraint in scheme.Constraints)
        {
            int earliest = int.MaxValue;
            for (int index = first; index < _traitDemands.Count; index++)
            {
                TraitDemand use = _traitDemands[index];
                if (TraitResolutionConstraintsEqual(PruneTraitConstraint(use.Constraint), PruneTraitConstraint(constraint)))
                {
                    earliest = Math.Min(earliest, use.Depth - _lambdaDepth);
                }
            }
            if (earliest == int.MaxValue)
            {
                retained.Add(constraint);
                continue;
            }
            if (earliest == 0 && DemandMentionsQuantifier(constraint, scheme))
            {
                if (_lambdaDepth > 0)
                {
                    held.UnionWith(DemandVariables(constraint));
                    RequireTraitConstraint(constraint);
                    continue;
                }
                _immediateEvidenceError = true;
                ReportConstraintBoundaryDiagnostic(GetSpan(binding.Value),
                    $"Trait requirement '{FormatTraitConstraint(constraint)}' is needed while evaluating this binding. Add a type annotation to determine its instance.",
                    allowRedundantWritten: false);
            }
            retained.Add(constraint);
            summary.Add(new TraitDemand(earliest, constraint));
        }
        if (held.Count != 0)
        {
            scheme = new TypeScheme(scheme.Quantified.Where(variable => !held.Contains(variable.Id)).ToArray(), scheme.Body, retained);
        }
        if (summary.Count != 0) _schemeDemands[scheme] = summary;
        return scheme;
    }

    private static IReadOnlySet<int> DemandVariables(TraitConstraint constraint) =>
        TraitTypeOperations.FreeVariables(new TypeScheme([], new TypeRef.TTuple(constraint.TypeArgs)));

    private static bool DemandMentionsQuantifier(TraitConstraint constraint, TypeScheme scheme)
    {
        IReadOnlySet<int> variables = DemandVariables(constraint);
        return scheme.Quantified.Any(variable => variables.Contains(variable.Id));
    }
}
