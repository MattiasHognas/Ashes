using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// Two functions lowered alike walk a node through its wrappers and return a constructor's binders
/// in a tuple, as stage 1's `lambdaParts` does. Their temps are numbered alike, and the set of
/// released pattern-owner duplicates was shared by every function, so the later function's tuple
/// kept the duplicate it took of the body beside its own reference.
/// </summary>
internal sealed class PatternOwnerDuplicateInLaterFunctionTemplate() : LeakCombinationTemplate(
    "leak.pattern-owner-duplicate-in-later-function", "laterFunctionOwnerDup", GeneratedFeature.Adt, GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string node = names.Of("LeakNode");
        string first = names.Of("firstParts");
        string later = names.Of("laterParts");
        string depth = names.Of("depth");
        string build = names.Of("build");
        string wrapper = names.Pick("At", "Located", "Spanned");
        string parameter = names.Pick("parameter", "name", "label");
        bool laterOnly = names.Coin("later-only");
        string parts = $$"""
            let recursive {{first}} node =
                match node with
                    | {{wrapper}}(_span, inner) -> {{first}}(inner)
                    | Lam({{parameter}}, body) -> Some(({{parameter}}, body))
                    | _ -> None

            let recursive {{later}} node =
                match node with
                    | {{wrapper}}(_span, inner) -> {{later}}(inner)
                    | Lam({{parameter}}, body) -> Some(({{parameter}}, body))
                    | _ -> None
            """;
        string measure = laterOnly
            ? $$"""
                match {{later}}(made) with
                    | Some((b, later)) -> Ashes.Text.byteLength(b) + {{depth}}(later)
                    | None -> 0
                """
            : $$"""
                match ({{first}}(made), {{later}}(made)) with
                    | (Some((a, first)), Some((b, later))) -> Ashes.Text.byteLength(a) + Ashes.Text.byteLength(b) + {{depth}}(first) + {{depth}}(later)
                    | _ -> 0
                """;
        return $$"""
            type {{node}} =
                | {{wrapper}}(Int, {{node}})
                | Lam(Str, {{node}})
                | Leaf(Int)

            {{parts}}

            let recursive {{depth}} (node: {{node}}) =
                match node with
                    | {{wrapper}}(_span, inner) -> {{depth}}(inner)
                    | Lam(_parameter, body) -> 1 + {{depth}}(body)
                    | Leaf(n) -> n

            let recursive {{build}} (k: Int) (n: Int) =
                if k == 0
                then Leaf(n)
                else {{wrapper}}(k, Lam(Ashes.Text.fromInt(n + k), {{build}}(k - 1)(n)))

            let {{names.Entry}} (seed: Int) =
                let made = {{build}}({{names.Between(2, 4)}})(seed)
                in
            {{Indent(measure, 8)}}
            """;
    }

    private static string Indent(string text, int spaces)
        => string.Join("\n", text.Split('\n').Select(line => new string(' ', spaces) + line));
}
