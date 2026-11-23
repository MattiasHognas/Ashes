using System.Collections.Immutable;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private readonly Dictionary<Expr, List<TraitDemand>> _recursiveDemands = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<Expr, ImmutableDictionary<string, CallbackOrigin>> _recursiveCallbackPeers = new(ReferenceEqualityComparer.Instance);

    private void RegisterRecursiveCallbackPeers(IReadOnlyList<(string Name, Expr Value)> bindings)
    {
        ImmutableDictionary<string, CallbackOrigin>.Builder peers = ImmutableDictionary.CreateBuilder<string, CallbackOrigin>(StringComparer.Ordinal);
        foreach ((string name, Expr value) in bindings)
        {
            peers[name] = new CallbackOrigin(-1, CallbackPath.Root, null, null) { Peer = value };
        }
        ImmutableDictionary<string, CallbackOrigin> scope = peers.ToImmutable();
        foreach ((_, Expr value) in bindings)
        {
            _recursiveCallbackPeers[value] = scope;
        }
    }

    // Install every provisional profile before walking any body. References crossing a cycle
    // therefore read the current joint summaries, just like monomorphic recursive type slots.
    private void ResolveRecursiveCallbackUses(ImmutableDictionary<string, CallbackOrigin> peers)
    {
        Dictionary<Expr, HashSet<CallbackUse>> members = new(ReferenceEqualityComparer.Instance);
        foreach (CallbackOrigin peer in peers.Values)
        {
            HashSet<CallbackUse> provisional = [];
            members[peer.Peer!] = provisional;
            _callbackUses[peer.Peer!] = provisional;
        }
        int acyclicRounds = members.Count;
        bool changed;
        do
        {
            changed = false;
            acyclicRounds--;
            foreach ((Expr member, HashSet<CallbackUse> current) in members)
            {
                HashSet<CallbackUse> found = [];
                CollectSourceCallbackUses(member, peers, found);
                foreach (CallbackUse use in found)
                {
                    changed |= MergeRecursiveCallbackUse(current, use, acyclicRounds < 0);
                }
            }
        }
        while (changed);
    }

    private static bool MergeRecursiveCallbackUse(HashSet<CallbackUse> uses, CallbackUse candidate, bool widenPaths)
    {
        // Repeating the same application in a later recursive stage cannot move a dictionary
        // requirement earlier. Keep the earliest stage for each source use.
        if (widenPaths && candidate.Path != CallbackPath.Root && !uses.Contains(candidate) &&
            uses.Any(use => use.Parameter == candidate.Parameter && use.Applied == candidate.Applied &&
                use.Stage == candidate.Stage && use.Returned == candidate.Returned && use.Path != candidate.Path))
        {
            // A cycle may repeatedly descend an aggregate. Beyond the group's simple paths,
            // project this formal onto its nested callbacks as standalone inference does.
            candidate = candidate with { Path = CallbackPath.Any };
        }
        CallbackUse? previous = uses.FirstOrDefault(use => use.Parameter == candidate.Parameter
            && use.Path == candidate.Path && use.Applied == candidate.Applied && use.Returned == candidate.Returned);
        if (previous is not null && previous.Stage <= candidate.Stage)
        {
            return false;
        }
        if (previous is not null)
        {
            uses.Remove(previous);
        }
        return uses.Add(candidate);
    }

    private (int, TypeRef) LowerLetRecursiveValue(Expr.LetRecursive binding, Expr.Lambda? innerLambda,
        TypeRef recursiveType, LoweredValueRequest request, ResolvedBindingSignature? signature)
    {
        // A recursive call sees the implementation closure with its leading evidence parameters.
        // Its source reference denotes the logical signature, including through closure captures.
        TextSpan definition = AstSpans.GetLetRecursiveNameOrDefault(binding);
        bool recordSignature = _configuration.CollectHoverTypes && IsValidSpan(definition)
            && signature?.Type is not null;
        if (recordSignature)
        {
            _recursiveHoverSignatures[definition] = new TypeScheme([], signature!.Type!, signature.Requirements);
        }

        int first = _traitDemands.Count;
        (int, TypeRef) lowered = LowerLetRecursiveValueCore(binding, innerLambda, recursiveType, request);
        if (_collectInferredTraitElaboration)
        {
            RegisterRecursiveCallbackPeers([(binding.Name, binding.Value)]);
        }
        if (recordSignature)
        {
            _recursiveHoverSignatures.Remove(definition);
        }
        RememberRecursiveDemands(binding.Value, first);
        return lowered;
    }

    private void RememberRecursiveDemands(Expr value, int first)
    {
        if (!_collectInferredTraitElaboration)
        {
            return;
        }
        List<TraitDemand> direct = [];
        for (int index = first; index < _traitDemands.Count; index++)
        {
            TraitDemand use = _traitDemands[index];
            direct.Add(use with { Depth = use.Depth - _lambdaDepth });
        }
        _recursiveDemands[value] = direct;
    }

    // Sibling calls see monomorphic placeholders during inference. Once their types unify,
    // a requirement's argument variables are shared. Carry the requirement and the earliest
    // stage that executes it across the actual sibling references before generalization.
    private void PropagateRecursiveDemands(RecursiveGroupExpr group, IReadOnlyList<TraitConstraint>[] inferred)
    {
        if (!_collectInferredTraitElaboration)
        {
            return;
        }
        RegisterRecursiveCallbackPeers(group.Bindings);
        if (!inferred.Any(requirements => requirements.Count != 0))
        {
            return;
        }
        ImmutableDictionary<string, CallbackOrigin> siblings = ImmutableDictionary.Create<string, CallbackOrigin>(StringComparer.Ordinal);
        for (int index = 0; index < group.Bindings.Count; index++)
        {
            siblings = siblings.Add(group.Bindings[index].Name, new CallbackOrigin(index, CallbackPath.Root, null, null));
        }
        List<(int Caller, CallbackUse Use)>[] callers = new List<(int, CallbackUse)>[inferred.Length];
        List<TraitDemand>[] summaries = new List<TraitDemand>[inferred.Length];
        for (int index = 0; index < summaries.Length; index++)
        {
            callers[index] = [];
            summaries[index] = [];
        }
        for (int index = 0; index < summaries.Length; index++)
        {
            Expr value = group.Bindings[index].Value;
            HashSet<CallbackUse> uses = [];
            WalkCallbackUses(value, 0, 0, true, siblings, uses);
            foreach (CallbackUse use in uses)
            {
                callers[use.Parameter].Add((index, use));
            }
            AddRecursiveDirectRequirements(value, inferred[index], summaries[index]);
        }
        ResolveRecursiveDemands(callers, summaries);
        for (int index = 0; index < summaries.Length; index++)
        {
            inferred[index] = summaries[index].Select(use => use.Constraint).ToArray();
            _recursiveDemands[group.Bindings[index].Value] = summaries[index];
        }
    }

    private void AddRecursiveDirectRequirements(Expr value, IReadOnlyList<TraitConstraint> requirements, List<TraitDemand> summary)
    {
        _recursiveDemands.TryGetValue(value, out List<TraitDemand>? direct);
        foreach (TraitConstraint requirement in requirements)
        {
            TraitConstraint constraint = PruneTraitConstraint(requirement);
            int earliest = direct is null ? int.MaxValue : EarliestRecursiveDemand(constraint, direct);
            MergeRecursiveDemand(summary, new TraitDemand(earliest, constraint));
        }
    }

    private void ResolveRecursiveDemands(List<(int Caller, CallbackUse Use)>[] callers, List<TraitDemand>[] summaries)
    {
        Queue<int> pending = new(Enumerable.Range(0, summaries.Length));
        while (pending.TryDequeue(out int callee))
        {
            foreach ((int caller, CallbackUse use) in callers[callee])
            {
                bool changed = false;
                for (int index = 0; index < summaries[callee].Count; index++)
                {
                    TraitDemand demand = summaries[callee][index];
                    int stage = int.MaxValue;
                    if (demand.Depth != int.MaxValue &&
                        (use.Applied < 0 || demand.Depth <= use.Applied || use.Returned))
                    {
                        stage = use.Stage + (use.Applied < 0 ? 0 : Math.Max(0, demand.Depth - use.Applied));
                    }
                    changed |= MergeRecursiveDemand(summaries[caller], demand with { Depth = stage });
                }
                if (changed)
                {
                    pending.Enqueue(caller);
                }
            }
        }
    }

    private bool MergeRecursiveDemand(List<TraitDemand> summary, TraitDemand requirement)
    {
        for (int index = 0; index < summary.Count; index++)
        {
            TraitDemand previous = summary[index];
            if (!TraitResolutionConstraintsEqual(previous.Constraint, requirement.Constraint))
            {
                continue;
            }
            if (previous.Depth <= requirement.Depth)
            {
                return false;
            }
            summary[index] = requirement;
            return true;
        }
        summary.Add(requirement);
        return true;
    }

    private int EarliestRecursiveDemand(TraitConstraint constraint, List<TraitDemand> demands)
    {
        int earliest = int.MaxValue;
        foreach (TraitDemand demand in demands)
        {
            if (TraitResolutionConstraintsEqual(PruneTraitConstraint(demand.Constraint), constraint))
            {
                earliest = Math.Min(earliest, demand.Depth);
            }
        }
        return earliest;
    }

    private void RegisterRecursiveDemandScheme(Expr source, TypeScheme scheme)
    {
        if (!_collectInferredTraitElaboration)
        {
            return;
        }
        RegisterCallbackSource(source, scheme.Body, scheme);
        if (_recursiveDemands.TryGetValue(source, out List<TraitDemand>? demands))
        {
            _schemeDemands[scheme] = scheme.Constraints.Select(constraint =>
                new TraitDemand(EarliestRecursiveDemand(constraint, demands), constraint))
                .Where(use => use.Depth != int.MaxValue).ToArray();
        }
    }
}
