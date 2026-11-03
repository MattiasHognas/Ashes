using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A recursive function that is a loop (one arm calls itself in tail position) matches the result
/// of a non-tail call to itself, a `Maybe` of a string, and builds a new `Maybe` from it. The loop's
/// exit places its result on the reference-counted heap, so the inner call's result is a counted
/// value its caller has to release, although the call was lowered before that was known.
/// </summary>
internal sealed class LoopSelfCallAdtResultTemplate() : LeakCombinationTemplate(
    "leak.loop-self-call-adt-result", "loopSelfAdtResult", GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.NestedMatch)
{
    protected override string Declarations(LeakNames names)
    {
        string tree = names.Of("LTree");
        string at = names.Of("LAt");
        string leaf = names.Of("LVar");
        string bind = names.Of("LLet");
        string other = names.Of("LOther");
        string forwarded = names.Of("forwarded");
        string contains = names.Of("containsName");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string shadowed = names.Word();
        string matchedArm = names.Coin("keep-none-first")
            ? $"""
                            | None -> None
                            | Some(name) ->
                                if name == nested
                                then None
                                else Some(name)
            """
            : $"""
                            | Some(name) ->
                                if name == nested
                                then None
                                else Some(name)
                            | None -> None
            """;
        string body = names.Coin("wrapped")
            ? $"{at}(1, {bind}({shadowed}, {other}, {at}(2, {leaf}(name))))"
            : $"{bind}({shadowed}, {other}, {leaf}(name))";
        string use = names.Coin("read-name")
            ? $"| Some(found) -> if {contains}(found)([name, {names.Word()}]) then 1 else 0"
            : "| Some(_found) -> 1";
        return $$"""
            type {{tree}} =
                | {{at}}(Int, {{tree}})
                | {{leaf}}(Str)
                | {{bind}}(Str, {{tree}}, {{tree}})
                | {{other}}

            let recursive {{forwarded}} (body: {{tree}}) =
                match body with
                    | {{at}}(_span, inner) -> {{forwarded}}(inner)
                    | {{leaf}}(name) -> Some(name)
                    | {{bind}}(nested, _value, nestedBody) ->
                        match {{forwarded}}(nestedBody) with
            {{matchedArm}}
                    | _ -> None

            let recursive {{contains}} (name: Str) (names: List(Str)) =
                match names with
                    | [] -> false
                    | candidate :: rest -> candidate == name || {{contains}}(name)(rest)

            let {{probe}} (n: Int) =
                let name = Ashes.Text.fromInt(n)
                in
                    match {{forwarded}}({{body}}) with
                        {{use}}
                        | None -> 0

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
