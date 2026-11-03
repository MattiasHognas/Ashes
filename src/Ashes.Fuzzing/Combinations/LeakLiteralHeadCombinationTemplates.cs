using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A list literal of constructors over a helper's fresh string, passed to a walk that copies its
/// list parameter onto the reference-counted heap: the literal's arena cells hold the constructors'
/// only references, which go to owned slots.
/// </summary>
internal sealed class ArenaListLiteralFreshHeadTemplate() : LeakCombinationTemplate(
    "leak.arena-list-literal-fresh-head", "arenaListLiteralHead", GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string inst = names.Of("LeakInst");
        string jump = names.Of("LeakJump");
        string other = names.Of("LeakOther");
        string label = names.Of("label");
        string walk = names.Of("walk");
        string elements = names.Coin("three-elements")
            ? $"[{jump}({label}(seed)), {other}(1), {jump}({label}(seed + 1)), {jump}({label}(seed + 2))]"
            : $"[{jump}({label}(seed)), {jump}({label}(seed + 1))]";
        return $$"""
            type {{inst}} =
                | {{jump}}(Str)
                | {{other}}(Int)

            let {{label}} (n: Int) = {{names.Word()}} + Ashes.Text.fromInt(n)

            let recursive {{walk}} (insts: List({{inst}})) (count: Int) =
                match insts with
                    | [] -> count
                    | inst :: rest ->
                        match inst with
                            | {{jump}}(name) -> {{walk}}(rest)(count + Ashes.Text.byteLength(name))
                            | _ -> {{walk}}(rest)(count)

            let {{names.Entry}} (seed: Int) = {{walk}}({{elements}})(seed)
            """;
    }
}

/// <summary>
/// A generic walk passes a string bound out of a list element to a trait-constrained generic lookup
/// whose result has no layout there: the reference taken for that result goes to an owned slot,
/// released at the walk's back edge.
/// </summary>
internal sealed class ConstrainedCallOfUnresolvedResultTemplate() : LeakCombinationTemplate(
    "leak.constrained-call-of-unresolved-result", "constrainedUnresolved", GeneratedFeature.Match, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string inst = names.Of("LeakInst");
        string jump = names.Of("LeakJump");
        string other = names.Of("LeakOther");
        string lookup = names.Of("lookupAssociation");
        string count = names.Of("countRefs");
        string total = names.Of("total");
        string label = names.Of("label");
        return $$"""
            type {{inst}} =
                | {{jump}}(Str)
                | {{other}}(Int)

            let recursive {{lookup}} key entries =
                match entries with
                    | [] -> None
                    | (k, v) :: tail ->
                        if k == key
                        then Some(v)
                        else {{lookup}}(key)(tail)

            let recursive {{count}} instructions acc =
                match instructions with
                    | [] -> acc
                    | inst :: tail ->
                        match inst with
                            | {{jump}}(target) ->
                                let found =
                                    match {{lookup}}(target)(acc) with
                                        | Some(c) -> c + 1
                                        | None -> 1
                                in
                                    {{count}}(tail)(acc)
                            | _ -> {{count}}(tail)(acc)

            let recursive {{total}} entries sum =
                match entries with
                    | [] -> sum
                    | (_k, v) :: rest -> {{total}}(rest)(sum + v)

            let {{label}} (n: Int) = {{names.Word()}} + Ashes.Text.fromInt(n)

            let {{names.Entry}} (seed: Int) = {{total}}({{count}}([{{jump}}({{label}}(seed)), {{other}}(1), {{jump}}({{label}}(seed + 1))])([]))(seed)
            """;
    }
}
