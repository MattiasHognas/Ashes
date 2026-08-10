namespace Ashes.Semantics;

/// <summary>Pure operations over constrained schemes, shared by inference and coherence checking.</summary>
internal static class TraitTypeOperations
{
    internal static TypeScheme Generalize(
        TypeRef body,
        IReadOnlyList<TraitConstraint> constraints,
        IReadOnlySet<int> environmentFreeVariables)
    {
        TypeScheme unquantified = new([], body, constraints);
        int[] quantifiedIds = FreeVariables(unquantified)
            .Where(variable => !environmentFreeVariables.Contains(variable))
            .OrderBy(variable => variable)
            .ToArray();
        return new TypeScheme(
            quantifiedIds.Select(variable => new TypeVar(variable, $"t{variable}")).ToArray(),
            body,
            constraints);
    }

    internal static InstantiatedTypeScheme Instantiate(TypeScheme scheme, Func<TypeRef> freshTypeVariable)
    {
        Dictionary<int, TypeRef> substitution = scheme.Quantified.ToDictionary(
            variable => variable.Id,
            _ => freshTypeVariable());
        return new InstantiatedTypeScheme(
            Substitute(scheme.Body, substitution),
            TraitConstraint.Canonicalize(scheme.Constraints.Select(constraint => Substitute(constraint, substitution))));
    }

    internal static TypeRef Substitute(TypeRef type, IReadOnlyDictionary<int, TypeRef> substitution)
    {
        return type switch
        {
            TypeRef.TVar variable => substitution.TryGetValue(variable.Id, out TypeRef? replacement) ? replacement : type,
            TypeRef.TFun function => new TypeRef.TFun(
                Substitute(function.Arg, substitution),
                Substitute(function.Ret, substitution))
            {
                Row = function.Row is null ? null : Substitute(function.Row, substitution),
            },
            TypeRef.TRow row => new TypeRef.TRow(
                row.Capabilities.Select(capability => (TypeRef.TCapability)Substitute(capability, substitution)).ToArray(),
                row.Tail is null ? null : Substitute(row.Tail, substitution)),
            TypeRef.TCapability capability => new TypeRef.TCapability(
                capability.Symbol,
                capability.Args.Select(argument => Substitute(argument, substitution)).ToArray()),
            TypeRef.TPtr pointer => new TypeRef.TPtr(Substitute(pointer.Pointee, substitution)),
            TypeRef.TList list => new TypeRef.TList(Substitute(list.Element, substitution)),
            TypeRef.TTuple tuple => new TypeRef.TTuple(tuple.Elements.Select(element => Substitute(element, substitution)).ToArray()),
            TypeRef.TNamedType named => new TypeRef.TNamedType(
                named.Symbol,
                named.TypeArgs.Select(argument => Substitute(argument, substitution)).ToArray()),
            _ => type,
        };
    }

    internal static TraitConstraint Substitute(
        TraitConstraint constraint,
        IReadOnlyDictionary<int, TypeRef> substitution)
    {
        return new TraitConstraint(
            constraint.Trait,
            constraint.TypeArgs.Select(argument => Substitute(argument, substitution)).ToArray());
    }

    internal static TypeScheme SubstituteScheme(
        TypeScheme scheme,
        IReadOnlyDictionary<int, TypeRef> substitution,
        IReadOnlyList<TypeVar>? replacementQuantifiers = null)
    {
        return new TypeScheme(
            replacementQuantifiers ?? scheme.Quantified,
            Substitute(scheme.Body, substitution),
            scheme.Constraints.Select(constraint => Substitute(constraint, substitution)).ToArray());
    }

    internal static IReadOnlySet<int> FreeVariables(TypeScheme scheme)
    {
        HashSet<int> variables = [];
        AddFreeVariables(scheme.Body, variables);
        foreach (TraitConstraint constraint in scheme.Constraints)
        {
            foreach (TypeRef argument in constraint.TypeArgs)
            {
                AddFreeVariables(argument, variables);
            }
        }
        variables.ExceptWith(scheme.Quantified.Select(variable => variable.Id));
        return variables;
    }

    internal static bool Occurs(int variableId, TraitConstraint constraint)
    {
        return constraint.TypeArgs.Any(argument => Occurs(variableId, argument));
    }

    internal static bool Occurs(int variableId, TypeRef type) => type switch
    {
        TypeRef.TVar variable => variable.Id == variableId,
        TypeRef.TFun function => Occurs(variableId, function.Arg)
            || Occurs(variableId, function.Ret)
            || (function.Row is not null && Occurs(variableId, function.Row)),
        TypeRef.TRow row => row.Capabilities.Any(capability => Occurs(variableId, capability))
            || (row.Tail is not null && Occurs(variableId, row.Tail)),
        TypeRef.TCapability capability => capability.Args.Any(argument => Occurs(variableId, argument)),
        TypeRef.TPtr pointer => Occurs(variableId, pointer.Pointee),
        TypeRef.TList list => Occurs(variableId, list.Element),
        TypeRef.TTuple tuple => tuple.Elements.Any(element => Occurs(variableId, element)),
        TypeRef.TNamedType named => named.TypeArgs.Any(argument => Occurs(variableId, argument)),
        _ => false,
    };

    private static void AddFreeVariables(TypeRef type, HashSet<int> variables)
    {
        switch (type)
        {
            case TypeRef.TVar variable:
                variables.Add(variable.Id);
                break;
            case TypeRef.TFun function:
                AddFreeVariables(function.Arg, variables);
                AddFreeVariables(function.Ret, variables);
                if (function.Row is not null)
                {
                    AddFreeVariables(function.Row, variables);
                }
                break;
            case TypeRef.TRow row:
                foreach (TypeRef.TCapability capability in row.Capabilities)
                {
                    AddFreeVariables(capability, variables);
                }
                if (row.Tail is not null)
                {
                    AddFreeVariables(row.Tail, variables);
                }
                break;
            case TypeRef.TCapability capability:
                foreach (TypeRef argument in capability.Args)
                {
                    AddFreeVariables(argument, variables);
                }
                break;
            case TypeRef.TPtr pointer:
                AddFreeVariables(pointer.Pointee, variables);
                break;
            case TypeRef.TList list:
                AddFreeVariables(list.Element, variables);
                break;
            case TypeRef.TTuple tuple:
                foreach (TypeRef element in tuple.Elements)
                {
                    AddFreeVariables(element, variables);
                }
                break;
            case TypeRef.TNamedType named:
                foreach (TypeRef argument in named.TypeArgs)
                {
                    AddFreeVariables(argument, variables);
                }
                break;
        }
    }
}

/// <summary>A scheme instantiated with one substitution, retaining its corresponding constraints.</summary>
internal sealed record InstantiatedTypeScheme(TypeRef Body, IReadOnlyList<TraitConstraint> Constraints);
