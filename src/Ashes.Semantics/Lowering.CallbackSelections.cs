using System.Collections.Immutable;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private sealed record CallbackPath(CallbackPath? Parent, int Index, string? Field)
    {
        public static CallbackPath Root { get; } = new(null, -1, null);
        public static CallbackPath Any { get; } = new(null, -2, null);

        public CallbackPath At(int index) => this == Any ? Any : new(this, index, null);
        public CallbackPath At(string field) => this == Any ? Any : new(this, -1, field);

        public CallbackPath Under(CallbackPath prefix) =>
            this == Any || prefix == Any ? Any : Parent is null ? prefix : this with { Parent = Parent.Under(prefix) };
    }

    private sealed record SelectedDemand(CallbackPath Path, TraitDemand Demand);
    private readonly Dictionary<TypeScheme, IReadOnlyList<SelectedDemand>> _schemeSelectedDemands = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TypeRef, IReadOnlyList<SelectedDemand>> _instantiatedSelectedDemands = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Expr, TypeRef> _callbackReferenceTypes = new(ReferenceEqualityComparer.Instance);

    private void RecordCallbackReference(Expr variable, TypeRef type)
    {
        if (_collectInferredTraitElaboration &&
            (_instantiatedDemands.ContainsKey(type) || _instantiatedSelectedDemands.ContainsKey(type)))
        {
            _callbackReferenceTypes[variable] = type;
        }
    }

    private void InstantiateSelectedDemands(TypeScheme scheme, TypeRef type, IReadOnlyDictionary<int, TypeRef> substitution)
    {
        if (!_schemeSelectedDemands.TryGetValue(scheme, out IReadOnlyList<SelectedDemand>? demands))
        {
            return;
        }
        _instantiatedSelectedDemands[type] = demands.Select(selected => selected with
        {
            Demand = selected.Demand with { Constraint = TraitTypeOperations.Substitute(selected.Demand.Constraint, substitution) },
        }).ToArray();
    }

    private void RegisterSelectedDemands(Expr value, TypeRef type, TypeScheme scheme)
    {
        if (scheme.Constraints.Count == 0 || Prune(type) is TypeRef.TFun)
        {
            return;
        }
        List<SelectedDemand> demands = [];
        CollectSelectedDemands(value, type, CallbackPath.Root, demands);
        if (demands.Count != 0)
        {
            _schemeSelectedDemands[scheme] = demands;
        }
    }

    private void CollectSelectedDemands(Expr value, TypeRef? type, CallbackPath path, List<SelectedDemand> result)
    {
        switch (value)
        {
            case Expr.TupleLit tuple:
                for (int index = 0; index < tuple.Elements.Count; index++)
                {
                    CollectSelectedDemands(tuple.Elements[index], null, path.At(index), result);
                }
                return;
            case Expr.RecordLit record:
                foreach ((string field, Expr child) in record.Fields)
                {
                    CollectSelectedDemands(child, null, path.At(field), result);
                }
                return;
        }
        type ??= _callbackReferenceTypes.GetValueOrDefault(value);
        if (type is null)
        {
            return;
        }
        if (_instantiatedSelectedDemands.TryGetValue(Prune(type), out IReadOnlyList<SelectedDemand>? selected))
        {
            foreach (SelectedDemand component in selected)
            {
                result.Add(component with { Path = component.Path.Under(path) });
            }
        }
        else if (_instantiatedDemands.TryGetValue(Prune(type), out IReadOnlyList<TraitDemand>? direct))
        {
            foreach (TraitDemand demand in direct)
            {
                result.Add(new SelectedDemand(path, demand));
            }
        }
    }

    private static ImmutableDictionary<string, CallbackOrigin> BindCallbackPattern(Pattern pattern,
        CallbackOrigin origin, ImmutableDictionary<string, CallbackOrigin> scope)
    {
        switch (pattern)
        {
            case Pattern.Var variable:
                return scope.SetItem(variable.Name, origin);
            case Pattern.As alias:
                return BindCallbackPattern(alias.Inner, origin, scope.SetItem(alias.Name, origin));
            case Pattern.Tuple tuple:
                for (int index = 0; index < tuple.Elements.Count; index++)
                {
                    scope = BindCallbackPattern(tuple.Elements[index], origin with { Path = origin.Path.At(index) }, scope);
                }
                break;
            case Pattern.Record record:
                foreach ((string field, Pattern child) in record.Fields)
                {
                    scope = BindCallbackPattern(child, origin with { Path = origin.Path.At(field) }, scope);
                }
                break;
        }
        return scope;
    }

    private static Expr? SelectCallbackValue(Expr value, CallbackPath path)
    {
        if (path.Parent is null)
        {
            return value;
        }
        return SelectCallbackValue(value, path.Parent) switch
        {
            Expr.TupleLit tuple when path.Index >= 0 && path.Index < tuple.Elements.Count => tuple.Elements[path.Index],
            Expr.RecordLit record when path.Field is not null => record.Fields.FirstOrDefault(
                field => string.Equals(field.Name, path.Field, StringComparison.Ordinal)).Value,
            _ => null,
        };
    }
}
