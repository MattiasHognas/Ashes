using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over indexed instructions that matches each one through a fresh tuple built only to be
/// matched by its shape, `(current, 0)`, while the instruction itself escapes into a call-rebuilt
/// accumulator. The tuple holds the instruction's pattern-owner duplicate, promoted to a retain once
/// the instruction escapes; the arena tuple releases nothing, and only the retains of loop-parameter
/// reads it stores were withdrawn, so every round leaked each instruction.
/// </summary>
internal sealed class ScrutineeOwnerDuplicateTemplate() : LeakCombinationTemplate(
    "leak.scrutinee-owner-duplicate", "scrutineeOwnerDuplicate", GeneratedFeature.Match, GeneratedFeature.List, GeneratedFeature.Record)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("Kind");
        string call = names.Of("Call");
        string other = names.Of("Other");
        string branch = names.Of("Branch");
        string instruction = names.Of("Instruction");
        string lookup = names.Of("lookupAssociation");
        string set = names.Of("setAssociation");
        string retarget = names.Of("retarget");
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

            let recursive {{lookup}} key entries =
                match entries with
                    | [] -> None
                    | (k, v) :: tail ->
                        if k == key
                        then Some(v)
                        else {{lookup}}(key)(tail)

            let recursive {{set}} key value entries =
                match entries with
                    | [] -> [(key, value)]
                    | (k, v) :: tail ->
                        if k == key
                        then (key, value) :: tail
                        else (k, v) :: {{set}}(key)(value)(tail)

            let {{retarget}} (stackEnv: Int) current =
                match current with
                    | {{instruction}} { instruction = {{call}}(dest, label, _) } -> {{instruction}}(instruction = {{call}}(dest, label, stackEnv))
                    | _ -> {{instruction}}(instruction = {{other}}(0))

            let recursive {{collect}} indexed remaining (nextTemp: Int) rewrites =
                match remaining with
                    | [] -> (rewrites, nextTemp)
                    | (position, current) :: tail ->
                        match (current, 0) with
                            | ({{instruction}} { instruction = _ }, _resultTemp) ->
                                let next = position + 1
                                in
                                    let retargeted =
                                        match {{lookup}}(next)(indexed) with
                                            | Some(nextCall) -> {{retarget}}(nextTemp)(nextCall)
                                            | None -> current
                                    in {{collect}}(indexed)(tail)(nextTemp + 1)({{set}}(next)(retargeted)(rewrites))
                            | _ -> {{collect}}(indexed)(tail)(nextTemp)(rewrites)

            let recursive {{index}} (items: List({{instruction}})) (n: Int) acc =
                match items with
                    | [] -> Ashes.Collection.List.reverse(acc)
                    | item :: rest -> {{index}}(rest)(n + 1)((n, item) :: acc)

            let {{once}} (items: List({{instruction}})) (tempCount: Int) =
                (let indexed = {{index}}(items)(0)([])
                in
                    match {{collect}}(indexed)(indexed)(tempCount)([]) with
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
