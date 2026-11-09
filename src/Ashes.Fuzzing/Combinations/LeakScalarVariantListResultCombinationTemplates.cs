using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function matching the owned result of a call that unwraps a syntax tree, and returning a list
/// of optional integers built from a part of it: the tree holds an optional integer too, so the list
/// may point into it, and the tree was kept instead of released behind an independent copy.
/// </summary>
internal sealed class ScalarVariantListResultTemplate() : LeakCombinationTemplate(
    "leak.scalar-variant-list-result", "scalarVariantListResult", GeneratedFeature.Adt, GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string span = names.Of("Span");
        string expr = names.Of("Node");
        string at = names.Of("NodeAt");
        string name = names.Of("NodeName");
        string call = names.Of("NodeCall");
        string tuple = names.Of("NodeTuple");
        string unspan = names.Of("unspan");
        string slotOf = names.Of("slotOf");
        string slotsOf = names.Of("slotsOf");
        string elementSlots = names.Of("elementSlots");
        string countSome = names.Of("countSome");
        string build = names.Of("build");
        bool spanRecord = names.Coin("span-record");
        int depth = names.Between(1, 3);
        string spanType = spanRecord ? span : "Int";
        string spanDecl = spanRecord ? $"type {span} =\n    | start: Int\n    | end: Int\n\n" : "";
        string spanOf(string start) => spanRecord ? $"{span}(start = {start}, end = {start} + 1)" : start;
        return $$"""
            {{spanDecl}}type {{expr}} =
                | {{at}}({{spanType}}, {{expr}})
                | {{name}}(Str)
                | {{call}}({{expr}}, {{expr}}, Maybe(Int))
                | {{tuple}}(List({{expr}}))

            let recursive {{unspan}} (expression: {{expr}}) =
                match expression with
                    | {{at}}(_span, inner) -> {{unspan}}(inner)
                    | other -> other

            let {{slotOf}} (element: {{expr}}) (base: Int) =
                match {{unspan}}(element) with
                    | {{name}}(_label) -> Some(base)
                    | _ -> None

            let recursive {{elementSlots}} (elements: List({{expr}})) (base: Int) =
                match elements with
                    | [] -> []
                    | element :: rest -> {{slotOf}}(element)(base) :: {{elementSlots}}(rest)(base)

            let {{slotsOf}} (scrutinee: {{expr}}) (base: Int) =
                match {{unspan}}(scrutinee) with
                    | {{tuple}}(elements) -> {{elementSlots}}(elements)(base)
                    | _ -> []

            let recursive {{countSome}} (slots: List(Maybe(Int))) =
                match slots with
                    | [] -> 0
                    | Some(_slot) :: rest -> 1 + {{countSome}}(rest)
                    | None :: rest -> {{countSome}}(rest)

            let recursive {{build}} (k: Int) (seed: Int) =
                if k == 0
                then {{call}}({{at}}({{spanOf("2")}}, {{name}}({{names.Word()}})), {{name}}({{names.Word()}}), Some(seed))
                else {{at}}({{spanOf("k")}}, {{build}}(k - 1)(seed))

            let {{names.Entry}} (seed: Int) =
                let tree = {{build}}({{LeakNames.Text(depth)}})(seed)
                in {{countSome}}({{slotsOf}}(tree)(seed)) + {{countSome}}({{slotsOf}}({{at}}({{spanOf("3")}}, {{tuple}}([{{name}}({{names.Word()}}), tree])))(seed))
            """;
    }
}
