using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop that walks a syntax tree through its wrapper nodes, each step handing on a pattern-bound
/// child as the next parameter, and returns <c>Some(node)</c> at its exit: the arena option cell
/// retained the parameter, and the result's copy retained it again.
/// </summary>
internal sealed class TransferredRootOptionResultTemplate() : LeakCombinationTemplate(
    "leak.transferred-root-option-result", "transferredRootOptionResult", GeneratedFeature.Adt, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string node = names.Of("Tree");
        string wrap = names.Of("TreeAt");
        string bind = names.Of("TreeLet");
        string leaf = names.Of("TreeName");
        string call = names.Of("TreeCall");
        string body = names.Of("chainBody");
        string size = names.Of("treeSize");
        string build = names.Of("buildTree");
        bool flag = names.Coin("flag-parameter");
        int depth = names.Between(1, 3);
        string flagParameter = flag ? " (lastOrdinary: Bool)" : "";
        string flagArgument = flag ? "(lastOrdinary)" : "";
        string flagStart = flag ? "(true)" : "";
        string exit = flag ? "if lastOrdinary\n            then Some(expression)\n            else None" : "Some(expression)";
        return $$"""
            type {{node}} =
                | {{wrap}}(Int, {{node}})
                | {{bind}}(Str, {{node}}, {{node}})
                | {{leaf}}(Str)
                | {{call}}({{node}}, {{node}})

            let recursive {{body}} (expression: {{node}}){{flagParameter}} =
                match expression with
                    | {{wrap}}(_span, inner) -> {{body}}(inner){{flagArgument}}
                    | {{bind}}(_name, _value, nested) -> {{body}}(nested){{flagArgument}}
                    | _ ->
                        {{exit}}

            let recursive {{size}} (expression: {{node}}) =
                match expression with
                    | {{wrap}}(_span, inner) -> 1 + {{size}}(inner)
                    | {{bind}}(_name, value, nested) -> {{size}}(value) + {{size}}(nested)
                    | {{call}}(callee, argument) -> {{size}}(callee) + {{size}}(argument)
                    | _ -> 1

            let recursive {{build}} (k: Int) (seed: Int) =
                if k == 0
                then {{call}}({{wrap}}(seed, {{leaf}}({{names.Word()}})), {{leaf}}({{names.Word()}}))
                else {{wrap}}(k, {{build}}(k - 1)(seed))

            let {{names.Entry}} (seed: Int) =
                match {{body}}({{bind}}({{names.Word()}}, {{leaf}}({{names.Word()}}), {{build}}({{LeakNames.Text(depth)}})(seed))){{flagStart}} with
                    | Some(found) -> {{size}}(found) + seed
                    | None -> seed
            """;
    }
}
