using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A list rebuilt by a match whose arms are the empty list and a tuple consed onto the function's
/// own recursive call, the tuple's parts reference-counted syntax trees: the join is fresh, and each
/// cell takes the recursive result over from the slot that keeps it.
/// </summary>
internal sealed class NilBesideFreshCellTemplate() : LeakCombinationTemplate(
    "leak.nil-beside-fresh-cell", "nilBesideFreshCell", GeneratedFeature.Adt, GeneratedFeature.Tuple, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string pattern = names.Of("CasePattern");
        string expr = names.Of("CaseExpr");
        string at = names.Of("ExprAt");
        string number = names.Of("ExprNumber");
        string name = names.Of("ExprName");
        string matchExpr = names.Of("ExprMatch");
        string bind = names.Of("PatternBind");
        string wildcard = names.Of("PatternAny");
        string patternAt = names.Of("PatternAt");
        string rewrite = names.Of("rewrite");
        string rewriteCases = names.Of("rewriteCases");
        string size = names.Of("size");
        string casesSize = names.Of("casesSize");
        bool guarded = names.Coin("guard-field");
        bool wrapped = names.Coin("wrapped-root");
        int arms = names.Between(1, 3);
        string element = guarded ? $"({pattern}, {expr}, Maybe({expr}))" : $"({pattern}, {expr})";
        string binders = guarded ? "(found, body, guard)" : "(found, body)";
        string sizeBinders = guarded ? "(_found, body, _guard)" : "(_found, body)";
        string tail = guarded ? ", None)" : ")";
        string cases = "[" + string.Join(", ", Enumerable.Range(0, arms).Select(index => index % 2 == 0
            ? $"({bind}({names.Word()}), {at}({LeakNames.Text(index)}, {number}(seed)){tail}"
            : $"({patternAt}(seed, {wildcard}), {name}({names.Word()}){tail}")) + "]";
        string root = $"{matchExpr}({at}(1, {name}({names.Word()})), {cases}, None)";
        string tree = wrapped ? $"{at}(seed, {root})" : root;
        return $$"""
            type {{pattern}} =
                | {{bind}}(Str)
                | {{wildcard}}
                | {{patternAt}}(Int, {{pattern}})

            type {{expr}} =
                | {{at}}(Int, {{expr}})
                | {{number}}(Int)
                | {{name}}(Str)
                | {{matchExpr}}({{expr}}, List({{element}}), Maybe(Int))

            let recursive {{rewrite}} (expression: {{expr}}) =
                match expression with
                    | {{at}}(span, inner) -> {{at}}(span)({{rewrite}}(inner))
                    | {{matchExpr}}(scrutinee, cases, position) -> {{matchExpr}}(scrutinee)({{rewriteCases}}(cases))(position)
                    | other -> other
            and {{rewriteCases}} (cases: List({{element}})) =
                match cases with
                    | [] -> []
                    | {{binders}} :: rest -> {{binders}} :: {{rewriteCases}}(rest)

            let recursive {{size}} (expression: {{expr}}) =
                match expression with
                    | {{at}}(_span, inner) -> 1 + {{size}}(inner)
                    | {{matchExpr}}(scrutinee, cases, _position) -> {{size}}(scrutinee) + {{casesSize}}(cases)
                    | _ -> 1
            and {{casesSize}} (cases: List({{element}})) =
                match cases with
                    | [] -> 0
                    | {{sizeBinders}} :: rest -> {{size}}(body) + {{casesSize}}(rest)

            let {{names.Entry}} (seed: Int) = {{size}}({{rewrite}}({{tree}}))
            """;
    }
}
