using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop generic in its element type (a pass-through parameter it never reads keeps it so) that
/// matches each element through a fresh tuple built only for its shape, `(current, 0)` against
/// `(Instruction { kind = _ }, _)`, while the element escapes into an accumulator. Matched against a
/// type variable, the shape counted as binding a heap value, so the tuple was not taken for a
/// borrowed scrutinee and the element's promoted pattern-owner duplicate it holds was never
/// withdrawn: every round leaked each element.
/// </summary>
internal sealed class GenericShapeScrutineeTemplate() : LeakCombinationTemplate(
    "leak.generic-shape-scrutinee", "genericShapeScrutinee", GeneratedFeature.Match, GeneratedFeature.List, GeneratedFeature.Record)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("Kind");
        string call = names.Of("Call");
        string other = names.Of("Other");
        string branch = names.Of("Branch");
        string instruction = names.Of("Instruction");
        string collect = names.Of("collect");
        string index = names.Of("index");
        string once = names.Of("once");
        string weigh = names.Of("weigh");
        string word = names.Word();
        return $$"""
            type {{kind}} =
                | {{call}}(Int, Str, Int)
                | {{other}}(Int)
                | {{branch}}(Int, List(Int))

            type {{instruction}} =
                | instruction: {{kind}}

            let recursive {{collect}} carried remaining (nextTemp: Int) rewrites =
                match remaining with
                    | [] -> (rewrites, nextTemp)
                    | (position, current) :: tail ->
                        match (current, 0) with
                            | ({{instruction}} { instruction = _ }, _resultTemp) -> {{collect}}(carried)(tail)(nextTemp + 1)((position + 1, current) :: rewrites)
                            | _ -> {{collect}}(carried)(tail)(nextTemp)(rewrites)

            let recursive {{index}} (items: List({{instruction}})) (n: Int) acc =
                match items with
                    | [] -> Ashes.Collection.List.reverse(acc)
                    | item :: rest -> {{index}}(rest)(n + 1)((n, item) :: acc)

            let {{once}} (items: List({{instruction}})) (tempCount: Int) =
                (let indexed = {{index}}(items)(0)([])
                in
                    match {{collect}}([])(indexed)(tempCount)([]) with
                        | (rewrites, nextTemp) -> (items, nextTemp > 0 && Ashes.Collection.List.length(rewrites) > 0))

            let recursive {{weigh}} (items: List({{instruction}})) =
                match items with
                    | [] -> 0
                    | {{instruction}} { instruction = {{call}}(_, label, _) } :: rest -> Ashes.Text.byteLength(label) + {{weigh}}(rest)
                    | _ :: rest -> 1 + {{weigh}}(rest)

            let {{names.Entry}} (seed: Int) =
                (let items = [{{instruction}}(instruction = {{call}}(1, {{word}} + Ashes.Text.fromInt(seed), 2)), {{instruction}}(instruction = {{call}}(5, "t" + Ashes.Text.fromInt(seed), 6)), {{instruction}}(instruction = {{call}}(9, "u", 1)), {{instruction}}(instruction = {{other}}(seed))]
                in
                    match {{once}}(items)(10) with
                        | (rewritten, _) -> {{weigh}}(rewritten))
            """;
    }
}
