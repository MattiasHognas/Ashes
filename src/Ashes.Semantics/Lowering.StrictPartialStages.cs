using System.Collections.Immutable;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // Split a curried source definition at a strict prefix that computes before returning
    // the next lambda. Both helpers are declared next to the original function, so their
    // free names resolve in its lexical scope. The original stays available at its usual ABI.
    private sealed record StrictStage(string Prefix, string Resume, int Arity, Expr PrefixBody, Expr ResumeBody)
    {
        public bool Used { get; private set; }
        public StrictStage? Forwarded { get; init; }

        public void MarkUsed()
        {
            Used = true;
            Forwarded?.MarkUsed();
        }
    }

    private readonly HashSet<(string Name, int Position)> _strictPartialSources = [];
    private readonly HashSet<(string Name, int Position)> _generalizedPartialCalls = [];

    private void NoteStrictPartialBinding(Expr.Let binding, TypeScheme scheme)
    {
        if (!_collectInferredTraitElaboration || scheme.Constraints.Count == 0) return;
        if (binding.Value is Expr.Call)
        {
            _generalizedPartialCalls.Add(TraitBindingKey(binding));
            return;
        }
        Expr cursor = binding.Value;
        while (cursor is Expr.Lambda lambda) cursor = lambda.Body;
        if (cursor is not Expr.Let) return;
        while (cursor is Expr.Let prefix) cursor = prefix.Body;
        if (cursor is Expr.Lambda) _strictPartialSources.Add(TraitBindingKey(binding));
    }

    private StrictStage? DescribeStrictStage(Expr value, int identity)
    {
        List<Expr.Lambda> parameters = [];
        while (value is Expr.Lambda parameter)
        {
            parameters.Add(parameter);
            value = parameter.Body;
        }
        List<Expr.Let> prefix = [];
        while (value is Expr.Let binding)
        {
            prefix.Add(binding);
            value = binding.Body;
        }
        if (parameters.Count == 0 || prefix.Count == 0 || value is not Expr.Lambda residual)
        {
            return null;
        }

        HashSet<string> reads = FreeVars(residual, []);
        string[] captured = parameters.Select(parameter => parameter.ParamName)
            .Concat(prefix.Select(binding => binding.Name))
            .Distinct(StringComparer.Ordinal)
            .Where(reads.Contains)
            .ToArray();
        string packedName = $"$partial_environment_{identity}";
        Expr pack = captured.Length switch
        {
            0 => new Expr.Var("Unit"),
            1 => new Expr.Var(captured[0]),
            _ => new Expr.TupleLit(captured.Select(name => (Expr)new Expr.Var(name)).ToArray()),
        };
        Expr resume = captured.Length switch
        {
            0 => new Expr.Lambda(packedName, residual),
            1 => new Expr.Lambda(captured[0], residual),
            _ => new Expr.Lambda(packedName, new Expr.Match(new Expr.Var(packedName),
                [new MatchCase(new Pattern.Tuple(captured.Select(name => (Pattern)new Pattern.Var(name)).ToArray()), residual)])),
        };
        for (int index = prefix.Count - 1; index >= 0; index--)
        {
            pack = CopyLetSpans(prefix[index], prefix[index] with { Body = pack });
        }
        for (int index = parameters.Count - 1; index >= 0; index--)
        {
            pack = CopyLambdaSpans(parameters[index], parameters[index] with { Body = pack });
        }
        return new StrictStage($"$partial_prefix_{identity}", $"$partial_resume_{identity}",
            parameters.Count, pack, CopySpan(residual, resume));
    }

    private Program ScheduleStrictPartialStages(Program program) =>
        _strictPartialSources.Count == 0 || _generalizedPartialCalls.Count == 0
            ? program : new StrictStageScheduler(this).Schedule(program);

    private sealed class StrictStageScheduler(Lowering discovery)
    {
        private int _next;
        private bool _changed;

        private StrictStage? StageFor(string name, Expr value, ImmutableDictionary<string, StrictStage> visible)
        {
            if (value is Expr.Var alias) return visible.GetValueOrDefault(alias.Name);
            if (discovery._strictPartialSources.Contains((name, AstSpans.GetOrDefault(value).Start)))
                return discovery.DescribeStrictStage(value, _next++);
            if (visible.IsEmpty || value is not Expr.Lambda) return null;

            // A curried wrapper may return another function's partial. Use that declaration's
            // prefix/resume pair so the source call runs at the wrapper's saturation point.
            List<Expr.Lambda> parameters = [];
            while (value is Expr.Lambda lambda)
            {
                parameters.Add(lambda);
                visible = visible.Remove(lambda.ParamName);
                value = lambda.Body;
            }
            List<Expr> arguments = [];
            if (CollectCallArgs(value, arguments) is not Expr.Var root ||
                !visible.TryGetValue(root.Name, out StrictStage? callee) || arguments.Count != callee.Arity)
                return null;
            Expr prefix = new Expr.Var(callee.Prefix);
            foreach (Expr argument in arguments) prefix = CopySpan(value, new Expr.Call(prefix, argument));
            for (int index = parameters.Count - 1; index >= 0; index--)
                prefix = CopyLambdaSpans(parameters[index], parameters[index] with { Body = prefix });
            int id = _next++;
            return new StrictStage($"$partial_prefix_{id}", $"$partial_resume_{id}", parameters.Count,
                prefix, new Expr.Var(callee.Resume)) { Forwarded = callee };
        }

        private Expr RewritePartial(string name, Expr value, ImmutableDictionary<string, StrictStage> visible)
        {
            if (visible.IsEmpty || value is not Expr.Call || !discovery._generalizedPartialCalls.Contains((name, AstSpans.GetOrDefault(value).Start))) return value;
            List<Expr> arguments = [];
            if (CollectCallArgs(value, arguments) is not Expr.Var root
                || !visible.TryGetValue(root.Name, out StrictStage? stage) || arguments.Count != stage.Arity)
            {
                return value;
            }
            stage.MarkUsed();
            _changed = true;
            Expr evaluated = new Expr.Var(stage.Prefix);
            foreach (Expr argument in arguments)
            {
                evaluated = CopySpan(value, new Expr.Call(evaluated, argument));
            }
            return CopySpan(value, new Expr.Call(new Expr.Var(stage.Resume), evaluated));
        }

        private Expr Visit(Expr expression, ImmutableDictionary<string, StrictStage> visible)
        {
            switch (expression)
            {
                case Expr.Let binding:
                    Expr rhs = RewritePartial(binding.Name, Visit(binding.Value, visible), visible);
                    StrictStage? stage = StageFor(binding.Name, rhs, visible);
                    Expr body = Visit(binding.Body, stage is null
                        ? visible.Remove(binding.Name) : visible.SetItem(binding.Name, stage));
                    Expr result = CopyLetSpans(binding, binding with { Value = rhs, Body = body });
                    if (stage is { Used: true } && rhs is not Expr.Var)
                    {
                        result = new Expr.Let(stage.Prefix, stage.PrefixBody,
                            new Expr.Let(stage.Resume, stage.ResumeBody, result));
                    }
                    return result;
                case Expr.LetRecursive recursive:
                    var recursiveScope = visible.Remove(recursive.Name);
                    return CopyLetRecursiveSpans(recursive, recursive with
                    {
                        Value = Visit(recursive.Value, recursiveScope),
                        Body = Visit(recursive.Body, recursiveScope),
                    });
                case Expr.Lambda lambda:
                    return CopyLambdaSpans(lambda, lambda with { Body = Visit(lambda.Body, visible.Remove(lambda.ParamName)) });
                case Expr.LetResult binding:
                    return CopyLetResultSpans(binding, binding with
                    {
                        Value = Visit(binding.Value, visible),
                        Body = Visit(binding.Body, visible.Remove(binding.Name)),
                    });
                case Expr.Match match:
                    return CopySpan(match, match with
                    {
                        Value = Visit(match.Value, visible),
                        Cases = match.Cases.Select(arm =>
                        {
                            var inner = visible.RemoveRange(PatternBindings(arm.Pattern));
                            return arm with { Body = Visit(arm.Body, inner), Guard = arm.Guard is null ? null : Visit(arm.Guard, inner) };
                        }).ToArray(),
                    });
                case Expr.Handle handle:
                    return CopySpan(handle, handle with
                    {
                        Body = Visit(handle.Body, visible),
                        Arms = handle.Arms.Select(arm => arm with
                        {
                            Body = Visit(arm.Body, visible.RemoveRange(arm.Parameters.SelectMany(PatternBindings))),
                        }).ToArray(),
                    });
                default:
                    return MapChildExpressions(expression, child => Visit(child, visible));
            }
        }

        public Program Schedule(Program program)
        {
            ImmutableDictionary<string, StrictStage> scope = ImmutableDictionary.Create<string, StrictStage>(StringComparer.Ordinal);
            List<(TopLevelItem Item, StrictStage? Stage)> scheduled = [];
            foreach (TopLevelItem item in program.Items)
            {
                switch (item)
                {
                    case TopLevelItem.LetDecl binding:
                        var visible = binding.IsRecursive ? scope.Remove(binding.Name) : scope;
                        Expr rhs = RewritePartial(binding.Name, Visit(binding.Value, visible), visible);
                        StrictStage? stage = binding.IsRecursive ? null : StageFor(binding.Name, rhs, scope);
                        scope = stage is null ? scope.Remove(binding.Name) : scope.SetItem(binding.Name, stage);
                        TopLevelItem.LetDecl rewritten = binding with { Value = rhs };
                        AstSpans.Set(rewritten, AstSpans.GetOrDefault(binding));
                        scheduled.Add((rewritten, rhs is Expr.Var ? null : stage));
                        break;
                    case TopLevelItem.RecursiveGroup group:
                        scope = scope.RemoveRange(group.Bindings.Select(member => member.Name));
                        var rewrittenGroup = group with
                        {
                            Bindings = group.Bindings.Select(member =>
                            (member.Name, Visit(member.Value, scope))).ToArray()
                        };
                        AstSpans.Set(rewrittenGroup, AstSpans.GetOrDefault(group));
                        AstSpans.SetRecursiveGroupBindingNames(rewrittenGroup, AstSpans.GetRecursiveGroupBindingNamesOrDefault(group));
                        scheduled.Add((rewrittenGroup, null));
                        break;
                    default:
                        scheduled.Add((item, null));
                        break;
                }
            }
            Expr? trailing = program.Body is null ? null : Visit(program.Body, scope);
            List<TopLevelItem> items = [];
            foreach ((TopLevelItem item, StrictStage? stage) in scheduled)
            {
                if (stage is { Used: true })
                {
                    items.Add(new TopLevelItem.LetDecl(stage.Prefix, stage.PrefixBody, false));
                    items.Add(new TopLevelItem.LetDecl(stage.Resume, stage.ResumeBody, false));
                }
                items.Add(item);
            }
            return _changed ? new Program(items, trailing) : program;
        }
    }
}
