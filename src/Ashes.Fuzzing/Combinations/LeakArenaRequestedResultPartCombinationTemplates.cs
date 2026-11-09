using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over indexed instructions whose accumulator a generic association update rebuilds, with
/// the loop's parameters annotated at concrete types so the accumulator is placed on the
/// reference-counted heap. The exit returns the accumulator inside an arena tuple beside a
/// parameter of unresolved type, so the result goes through the generic tuple-parts protocol, and
/// the caller asks for an arena result rather than the parts: the tuple's retain of the
/// accumulator sat in an owned slot the not-requested path never released, so every call's final
/// accumulator leaked whole.
/// </summary>
internal sealed class ArenaRequestedResultPartTemplate() : LeakCombinationTemplate(
    "leak.arena-requested-result-part", "arenaRequestedResultPart", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("Kind");
        string call = names.Of("Call");
        string other = names.Of("Other");
        string instruction = names.Of("Instruction");
        string set = names.Of("setAssociation");
        string collect = names.Of("collect");
        string index = names.Of("index");
        string word = names.Word();
        return $$"""
            type {{kind}} =
                | {{call}}(Int, Str, Int)
                | {{other}}(Int)

            type {{instruction}} =
                | kind: {{kind}}
                | note: Maybe(Str)

            let recursive {{set}} key value entries =
                match entries with
                    | [] -> [(key, value)]
                    | (k, v) :: tail ->
                        if k == key
                        then (key, value) :: tail
                        else (k, v) :: {{set}}(key)(value)(tail)

            let recursive {{collect}} (remaining: List((Int, {{instruction}}))) (count: Int) (expansions: List((Int, List({{instruction}})))) carried =
                match remaining with
                    | [] -> (expansions, carried, count)
                    | (position, current) :: tail ->
                        match current with
                            | {{instruction}} { kind = {{call}}(_, _, env), note = note } -> {{collect}}(tail)(count + 1)({{set}}(position)([{{instruction}}(kind = {{other}}(env), note = note)])(expansions))(carried)
                            | _ -> {{collect}}(tail)(count)(expansions)(carried)

            let recursive {{index}} (items: List({{instruction}})) (n: Int) acc =
                match items with
                    | [] -> Ashes.Collection.List.reverse(acc)
                    | item :: rest -> {{index}}(rest)(n + 1)((n, item) :: acc)

            let {{names.Entry}} (seed: Int) =
                (let items = [{{instruction}}(kind = {{call}}(1, "{{word}}" + Ashes.Text.fromInt(seed), 2), note = Some("a" + Ashes.Text.fromInt(seed))), {{instruction}}(kind = {{call}}(5, "t", 6), note = None), {{instruction}}(kind = {{other}}(seed), note = None)]
                in
                    match {{collect}}({{index}}(items)(0)([]))(0)([])([]) with
                        | (expansions, _carried, count) -> Ashes.Collection.List.length(expansions) + count)
            """;
    }
}
