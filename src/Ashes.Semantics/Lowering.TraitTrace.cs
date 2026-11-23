using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // Only called for the explicitly requested trait when its diagnostic is about to be reported.
    private void TraceAmbiguousTrait(TraitConstraint constraint, TextSpan span)
    {
        Console.Error.WriteLine($"[trait-ambiguity] goal={TraitConstraint.StableKey(PruneTraitConstraint(constraint))} source={TraceTraitLocation(span)} discovery={_collectInferredTraitElaboration} emit={_emitTraitDictionaries} elementDepth={_elementSpecializationDepth}");
        for (IrFunctionOrigin? origin = _activeFunctionOrigin; origin is not null;
            origin = origin.ParentGeneratedLabel is { } parent ? _irFunctionOriginsByLabel.GetValueOrDefault(parent) : null)
        {
            Console.Error.WriteLine($"  origin={origin.GeneratedLabel} kind={origin.Kind} name={origin.Source?.QualifiedName ?? origin.Source?.SourceName} declaration={origin.Source?.DeclarationLocation}");
        }
        foreach (ActiveTraitImplementationMethod method in _activeTraitImplementationMethods)
        {
            Console.Error.WriteLine($"  selected={TraitConstraint.StableKey(PruneTraitConstraint(method.Constraint))}.{method.Method} binding={method.BindingName}");
        }
        foreach ((string name, List<ActiveTraitDictionaryParameter> parameters) in _activeTraitDictionaryParameters)
        {
            foreach (ActiveTraitDictionaryParameter parameter in parameters)
            {
                string goal = parameter.Metadata.Constraint is { } active ? TraitConstraint.StableKey(PruneTraitConstraint(active)) : "unbound";
                Console.Error.WriteLine($"  ambient={name} parameter={parameter.ParameterName} goal={goal} binding={Lookup(parameter.ParameterName)?.GetType().Name ?? "absent"}");
            }
        }
        string prefix = $"{span.Start}:{span.End}:{constraint.Trait.Name}:";
        foreach ((string key, int count) in _pinnedAmbiguousTraitGoalOccurrences.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            int ordinal = count - 1;
            string occurrence = AmbiguousTraitGoalOccurrenceKey(key, ordinal);
            TraitConstraint? proof = _provenAmbiguousTraitGoalTypes.GetValueOrDefault(key) ?? _provenAmbiguousTraitGoalTypes.GetValueOrDefault(occurrence);
            Console.Error.WriteLine($"  replay={occurrence} proof={(proof is null ? "none" : TraitConstraint.StableKey(proof))}");
        }
        foreach (string name in _inliningInProgress)
        {
            Console.Error.WriteLine($"  inline={name}");
        }
        foreach (TextSpan caller in _diagnosticSpans.Distinct().Take(96))
        {
            Console.Error.WriteLine($"  caller={TraceTraitLocation(caller)}");
        }
    }

    private string TraceTraitLocation(TextSpan span) => ResolveSourceLocation(span) is { } location
        ? $"{location.FilePath}:{location.Line}:{location.Column} [{span.Start}..{span.End}]"
        : $"[{span.Start}..{span.End}]";
}
