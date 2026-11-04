using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop matches the `Some((definition, through))` a call returns, conses a value built from
/// `definition` onto one accumulator and, behind the flag `through`, an integer onto another. The
/// integer list's type could hold a part of the matched value, but its successor names nothing
/// bound from it (the flag is only tested), so the iteration's owned result has to be released at
/// the back edge.
/// </summary>
internal sealed class IterationSlotUnnamedSuccessorTemplate() : LeakCombinationTemplate(
    "leak.iteration-slot-unnamed-successor", "iterationSlotNames", GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.NestedMatch, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("IKind");
        string load = names.Of("ILoad");
        string make = names.Of("IMake");
        string call = names.Of("ICall");
        string other = names.Of("IOther");
        string caseType = names.Of("ICase");
        string switchCase = names.Of("ISwitch");
        string lookup = names.Of("lookupAssociation");
        string resolve = names.Of("resolve");
        string build = names.Of("tryBuild");
        string count = names.Of("count");
        string countKinds = names.Of("countKinds");
        string pass = names.Of("pass");
        string passFirst = names.Of("passFirst");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string condition = names.Coin("negated-flag") ? "!through" : "through";
        string tail = names.Coin("label-first") ? "[n, 1]" : "[1]";
        return $$"""
            type {{caseType}} =
                | tag: Int
                | label: Str

            type {{kind}} =
                | {{switchCase}}(Int, List({{caseType}}))
                | {{load}}(Int, Int)
                | {{make}}(Int, Str, List(Int))
                | {{call}}(Str, List(Int))
                | {{other}}

            let recursive {{lookup}} key entries =
                match entries with
                    | [] -> None
                    | (k, v) :: rest ->
                        if k == key
                        then Some(v)
                        else {{lookup}}(key)(rest)

            let {{resolve}} (defs: List((Int, {{kind}}))) (temp: Int) =
                match {{lookup}}(temp)(defs) with
                    | None -> None
                    | Some(def) ->
                        match def with
                            | {{load}}(_, slot) ->
                                match {{lookup}}(slot)(defs) with
                                    | Some(stored) -> Some((stored, true))
                                    | None -> Some((def, false))
                            | _ -> Some((def, false))

            let {{build}} (definition: {{kind}}) =
                match definition with
                    | {{make}}(_, label, args) -> Some({{call}}(label, args))
                    | _ -> None

            let recursive {{count}} (xs: List(Int)) (n: Int) =
                match xs with
                    | [] -> n
                    | _ :: rest -> {{count}}(rest)(n + 1)

            let recursive {{countKinds}} (kinds: List({{kind}})) (n: Int) =
                match kinds with
                    | [] -> n
                    | _ :: rest -> {{countKinds}}(rest)(n + 1)

            let recursive {{passFirst}} (defs: List((Int, {{kind}}))) (temps: List(Int)) (dead: List(Int)) (acc: List({{kind}})) =
                match temps with
                    | [] -> {{count}}(dead)(0) + {{countKinds}}(acc)(0)
                    | temp :: rest ->
                        match {{resolve}}(defs)(temp) with
                            | Some((definition, through)) ->
                                match {{build}}(definition) with
                                    | Some(known) ->
                                        let nextDead =
                                            if {{condition}}
                                            then temp :: dead
                                            else dead
                                        in {{passFirst}}(defs)(rest)(nextDead)(known :: acc)
                                    | None -> {{passFirst}}(defs)(rest)(dead)(acc)
                            | None -> {{passFirst}}(defs)(rest)(dead)(acc)

            let recursive {{pass}} (defs: List((Int, {{kind}}))) (temps: List(Int)) (dead: List(Int)) (acc: List({{kind}})) =
                match temps with
                    | [] -> {{count}}(dead)(0) + {{countKinds}}(acc)(0)
                    | temp :: rest ->
                        match {{resolve}}(defs)(temp) with
                            | Some((definition, through)) ->
                                match {{build}}(definition) with
                                    | Some(known) ->
                                        let nextDead =
                                            if {{condition}}
                                            then temp :: dead
                                            else dead
                                        in {{pass}}(defs)(rest)(nextDead)(known :: acc)
                                    | None -> {{pass}}(defs)(rest)(dead)(acc)
                            | None -> {{pass}}(defs)(rest)(dead)(acc)

            let {{probe}} (n: Int) =
                let defs = [(1, {{load}}(1, 5)), (5, {{make}}(n, Ashes.Text.fromInt(n), {{tail}})), (3, {{other}}), (7, {{switchCase}}(n, [{{caseType}}(tag = 1, label = "a")])), (6, {{make}}(2, Ashes.Text.fromInt(n + 1), [n]))]
                in {{passFirst}}(defs)([1, 3, 4, 6, 7])([])([]) + {{pass}}(defs)([1, 3, 4, 6, 7])([])([])

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
