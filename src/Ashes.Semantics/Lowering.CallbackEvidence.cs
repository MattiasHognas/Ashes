using System.Collections.Immutable;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // Source applications count from the declaration, including each curried parameter.
    // A returned formal can be applied again by the caller after this source stage.
    private sealed record CallbackUse(int Parameter, CallbackPath Path, int Applied, int Stage, bool Returned);
    private sealed record CallbackOrigin(int Parameter, CallbackPath Path, Expr? Value, ImmutableDictionary<string, CallbackOrigin>? Scope);

    private readonly Dictionary<TypeScheme, Expr> _schemeCallbackSources = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TypeRef, Expr> _instantiatedCallbackSources = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Expr, Expr> _callbackCallTargets = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Expr, IReadOnlySet<CallbackUse>> _callbackUses = new(ReferenceEqualityComparer.Instance);

    private void RegisterCallbackSource(Expr value, TypeRef type, TypeScheme scheme)
    {
        Expr? source = value is Expr.Lambda ? value : _instantiatedCallbackSources.GetValueOrDefault(Prune(type));
        if (source is not null)
        {
            _schemeCallbackSources[scheme] = source;
        }
    }

    private void NoteCallbackArgument(Expr root, IReadOnlyList<Expr> arguments, int ordinal, TypeRef argumentType, int first)
    {
        if (!_collectInferredTraitElaboration || _instantiatedDemands.Count == 0 && first == _traitDemands.Count)
        {
            return;
        }
        Expr argument = arguments[ordinal];
        if (argument is not (Expr.TupleLit or Expr.RecordLit) && first == _traitDemands.Count &&
            !_instantiatedDemands.ContainsKey(Prune(argumentType)) && !_instantiatedSelectedDemands.ContainsKey(Prune(argumentType)))
        {
            return;
        }
        List<SelectedDemand> demands = [];
        CollectSelectedDemands(argument, argumentType, CallbackPath.Root, demands);
        if (Prune(argumentType) is TypeRef.TFun)
        {
            // Creating a function argument can discover requirements in its body. Its caller
            // determines whether and when the body executes; eager argument effects stay here.
            for (int index = _traitDemands.Count - 1; index >= first; index--)
            {
                TraitDemand demand = _traitDemands[index];
                if (demand.Depth <= _lambdaDepth)
                {
                    continue;
                }
                demands.Add(new SelectedDemand(CallbackPath.Root, demand with { Depth = demand.Depth - _lambdaDepth }));
                _traitDemands.RemoveAt(index);
            }
        }
        if (demands.Count == 0)
        {
            return;
        }
        ProjectCallbackArgument(root, arguments.Count, ordinal, demands);
    }

    private void ProjectCallbackArgument(Expr root, int count, int ordinal, List<SelectedDemand> demands)
    {
        Expr? source = root as Expr.Lambda ?? _callbackCallTargets.GetValueOrDefault(root);
        if (source is null)
        {
            // A higher-order parameter with no source summary can invoke its callback now.
            foreach (SelectedDemand item in demands)
            {
                NoteMethodDemand(item.Demand.Constraint);
            }
            return;
        }
        foreach (CallbackUse use in CallbackUses(source))
        {
            if (use.Parameter != ordinal)
            {
                continue;
            }
            int applied = AppliedCallbackArguments(use, count);
            foreach (SelectedDemand item in demands)
            {
                if (item.Path != use.Path)
                {
                    continue;
                }
                TraitDemand demand = item.Demand;
                if (applied < 0 || demand.Depth <= applied || use.Returned)
                {
                    int residual = applied < 0 ? 0 : Math.Max(0, demand.Depth - applied);
                    NoteMethodDemand(demand.Constraint, Math.Max(0, use.Stage - count) + residual);
                }
            }
        }
    }

    private static int AppliedCallbackArguments(CallbackUse use, int supplied) =>
        use.Applied < 0 || supplied < 0 && use.Returned ? -1 :
        use.Applied + (use.Returned ? Math.Max(0, supplied - use.Stage) : 0);

    private IReadOnlySet<CallbackUse> CallbackUses(Expr source)
    {
        if (_callbackUses.TryGetValue(source, out IReadOnlySet<CallbackUse>? cached))
        {
            return cached;
        }
        HashSet<CallbackUse> uses = [];
        _callbackUses[source] = uses;
        ImmutableDictionary<string, CallbackOrigin> scope = ImmutableDictionary.Create<string, CallbackOrigin>(StringComparer.Ordinal);
        int arity = 0;
        while (source is Expr.Lambda lambda)
        {
            scope = scope.SetItem(lambda.ParamName, new CallbackOrigin(arity++, CallbackPath.Root, null, null));
            source = lambda.Body;
        }
        WalkCallbackUses(source, 0, arity, true, scope, uses);
        return uses;
    }

    private void WalkCallbackUses(Expr value, int applied, int depth, bool returned,
        ImmutableDictionary<string, CallbackOrigin> scope, HashSet<CallbackUse> uses)
    {
        switch (value)
        {
            case Expr.Var variable when scope.TryGetValue(variable.Name, out CallbackOrigin? origin):
                WalkCallbackOrigin(origin, applied, depth, returned, uses);
                break;
            case Expr.QualifiedVar field when scope.TryGetValue(field.Module, out CallbackOrigin? owner):
                WalkCallbackOrigin(owner with { Path = owner.Path.At(field.Name) }, applied, depth, returned, uses);
                break;
            case Expr.Lambda lambda when applied != 0 || returned:
                WalkCallbackUses(lambda.Body, applied < 0 ? -1 : Math.Max(0, applied - 1),
                    depth + (applied == 0 ? 1 : 0), returned, scope.Remove(lambda.ParamName), uses);
                break;
            case Expr.Lambda:
                break;
            case Expr.Call:
                WalkCallbackCall(value, applied, depth, returned, scope, uses);
                break;
            case Expr.Let binding:
                WalkCallbackUses(binding.Value, 0, depth, false, scope, uses);
                bool tracksValue = MayReferToCallback(binding.Value, scope);
                WalkCallbackUses(binding.Body, applied, depth, returned,
                    tracksValue ? scope.SetItem(binding.Name, CallbackOriginFor(binding.Value, scope)) : scope.Remove(binding.Name), uses);
                break;
            case Expr.LetRecursive binding:
                WalkCallbackUses(binding.Body, applied, depth, returned,
                    scope.SetItem(binding.Name, new CallbackOrigin(-1, CallbackPath.Root, binding.Value, scope.Remove(binding.Name))), uses);
                break;
            case Expr.If branch:
                WalkCallbackUses(branch.Cond, 0, depth, false, scope, uses);
                WalkCallbackUses(branch.Then, applied, depth, returned, scope, uses);
                WalkCallbackUses(branch.Else, applied, depth, returned, scope, uses);
                break;
            case Expr.Match match:
                WalkCallbackUses(match.Value, 0, depth, false, scope, uses);
                foreach (MatchCase arm in match.Cases)
                {
                    ImmutableDictionary<string, CallbackOrigin> local = scope.RemoveRange(PatternBindings(arm.Pattern));
                    CallbackOrigin target = CallbackOriginFor(match.Value, scope);
                    local = BindCallbackPattern(arm.Pattern, target, local);
                    if (arm.Guard is not null)
                    {
                        WalkCallbackUses(arm.Guard, 0, depth, false, local, uses);
                    }
                    WalkCallbackUses(arm.Body, applied, depth, returned, local, uses);
                }
                break;
            default:
                foreach (Expr child in EnumerateChildren(value))
                {
                    WalkCallbackUses(child, 0, depth, false, scope, uses);
                }
                break;
        }
    }

    private static CallbackOrigin CallbackOriginFor(Expr value, ImmutableDictionary<string, CallbackOrigin> scope) =>
        value is Expr.Var variable && scope.TryGetValue(variable.Name, out CallbackOrigin? origin)
            ? origin : new CallbackOrigin(-1, CallbackPath.Root, value, scope);

    private void WalkCallbackOrigin(CallbackOrigin origin, int applied, int depth, bool returned, HashSet<CallbackUse> uses)
    {
        if (origin.Value is null)
        {
            if (returned || applied != 0)
            {
                uses.Add(new CallbackUse(origin.Parameter, origin.Path, applied, depth, returned));
            }
        }
        else if (SelectCallbackValue(origin.Value, origin.Path) is { } selected)
        {
            WalkCallbackUses(selected, applied, depth, returned, origin.Scope!, uses);
        }
    }

    private static bool MayReferToCallback(Expr value, ImmutableDictionary<string, CallbackOrigin> scope) =>
        value switch
        {
            Expr.Var variable => scope.ContainsKey(variable.Name),
            Expr.QualifiedVar field => scope.ContainsKey(field.Module),
            Expr.IntLit or Expr.BoolLit or Expr.StrLit or Expr.FloatLit or Expr.RuneLit => false,
            _ => true,
        };

    private void WalkCallbackCall(Expr call, int applied, int depth, bool returned,
        ImmutableDictionary<string, CallbackOrigin> scope, HashSet<CallbackUse> uses)
    {
        List<Expr> arguments = [];
        Expr root = CollectCallArgs(call, arguments);
        int count = applied < 0 ? -1 : arguments.Count + applied;
        WalkCallbackUses(root, count, depth, returned, scope, uses);
        Expr? source = root as Expr.Lambda ?? _callbackCallTargets.GetValueOrDefault(root);
        for (int index = 0; index < arguments.Count; index++)
        {
            Expr argument = arguments[index];
            if (!MayReferToCallback(argument, scope))
            {
                continue;
            }
            WalkCallbackUses(argument, 0, depth, false, scope, uses);
            if (source is null)
            {
                WalkCallbackUses(argument, -1, depth, false, scope, uses);
                continue;
            }
            foreach (CallbackUse use in CallbackUses(source))
            {
                if (use.Parameter != index || count >= 0 && use.Stage > count && !returned)
                {
                    continue;
                }
                int delayed = count < 0 ? 0 : Math.Max(0, use.Stage - count);
                CallbackOrigin origin = CallbackOriginFor(argument, scope);
                WalkCallbackOrigin(origin with { Path = use.Path.Under(origin.Path) }, AppliedCallbackArguments(use, count), depth + delayed,
                    use.Returned && returned, uses);
            }
        }
    }
}
