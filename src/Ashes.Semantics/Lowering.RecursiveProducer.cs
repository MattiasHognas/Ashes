using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private void RecordLetProducerProvenance(int slot, Expr value)
    {
        _letBindingValues[slot] = value;
        if (IsRecursiveProducerTail(value))
        {
            _recursiveProducerResultSlots.Add(slot);
        }
    }

    // Resolve each let while its defining lexical scope is still active, then retain the result
    // by frame-local slot. Re-reading the original AST at a use would misresolve shadowed names.
    // This is producer provenance, not uniqueness: aliases may still share their result, and the
    // cons construction must acquire its own reference from an existing owner.
    private bool IsRecursiveProducerResult(Expr expression, Dictionary<string, bool>? lexicalResults = null)
    {
        switch (expression)
        {
            case Expr.Var variable:
                if (lexicalResults is not null && lexicalResults.TryGetValue(variable.Name, out bool recursiveResult))
                {
                    return recursiveResult;
                }
                return Lookup(variable.Name) switch
                {
                    Binding.Local local => _recursiveProducerResultSlots.Contains(local.Slot),
                    Binding.Scheme scheme => _recursiveProducerResultSlots.Contains(scheme.Slot),
                    _ => false,
                };
            case Expr.Let binding:
                bool valueIsRecursiveResult = IsRecursiveProducerResult(binding.Value, lexicalResults);
                Dictionary<string, bool> bodyResults = CopyProducerLexicalResults(lexicalResults);
                bodyResults[binding.Name] = valueIsRecursiveResult;
                return IsRecursiveProducerResult(binding.Body, bodyResults);
            case Expr.If conditional:
                return IsRecursiveProducerResult(conditional.Then, lexicalResults)
                    && IsRecursiveProducerResult(conditional.Else, lexicalResults);
            case Expr.Match match:
                if (match.Cases.Count == 0)
                {
                    return false;
                }
                foreach (MatchCase arm in match.Cases)
                {
                    HashSet<string> binders = new(StringComparer.Ordinal);
                    CollectPatternBinders(arm.Pattern, binders);
                    Dictionary<string, bool> armResults = CopyProducerLexicalResults(lexicalResults);
                    foreach (string binder in binders)
                    {
                        armResults[binder] = false;
                    }
                    if (!IsRecursiveProducerResult(arm.Body, armResults))
                    {
                        return false;
                    }
                }
                return true;
            case Expr.Call:
                Expr root = expression;
                while (root is Expr.Call call)
                {
                    root = call.Func;
                }
                return root is Expr.Var callee
                    && (lexicalResults is null || !lexicalResults.ContainsKey(callee.Name))
                    && Lookup(callee.Name) is Binding.Self;
            default:
                return false;
        }
    }

    private static Dictionary<string, bool> CopyProducerLexicalResults(Dictionary<string, bool>? lexicalResults)
        => lexicalResults is null
            ? new(StringComparer.Ordinal)
            : new(lexicalResults, StringComparer.Ordinal);
}
