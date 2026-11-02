using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A generic body hands a fresh value, a helper's record or a foreign call's Unit, to a closure
/// parameter whose result has no layout there, and returns something else: the closure does not
/// adopt the value, so the body releases it once its own result is out of that call's reach.
/// </summary>
internal sealed class FreshArgumentToClosureOfUnresolvedResultTemplate() : LeakCombinationTemplate(
    "leak.fresh-argument-to-closure-of-unresolved-result", "freshArgumentToClosure", GeneratedFeature.Lambda)
{
    protected override string Declarations(LeakNames names)
    {
        string box = names.Of("LeakBox");
        string absOf = names.Of("absOf");
        string seedOf = names.Of("seedOf");
        string position = names.Of("positionAt");
        string branch = names.Of("branch");
        string arm = names.Of("emitArm");
        string made = names.Coin("unit-argument")
            ? $"{seedOf}(builder + block)"
            : $"{box}({absOf}(builder + block))";
        return $$"""
            external {{absOf}}(Int) -> Int = "abs@libc.so.6"

            external {{seedOf}}(Int) -> void = "srandom@libc.so.6"

            type {{box}} =
                | {{box}}(Int)

            let {{position}} builder block = {{made}}

            let {{branch}} builder target = {{absOf}}(builder - target)

            let {{arm}} builder block continueBlock body =
                block
                |> {{position}}(builder)
                |> body
                |> (given (_) -> {{branch}}(builder)(continueBlock))

            let {{names.Entry}} (seed: Int) = {{arm}}(seed)({{names.Between(1, 4)}})(3)(given (_) -> seed + 7)
            """;
    }
}

/// <summary>
/// A recursive function holding an owned call result whose own result (a <c>Maybe(Int)</c> the arena
/// has no copy for) cannot hold it: the result stays where it is, since its self-calls, lowered
/// before its return, never take a reference-counted copy as owned.
/// </summary>
internal sealed class SelfCallResultKeptInPlaceTemplate() : LeakCombinationTemplate(
    "leak.self-call-result-kept-in-place", "selfCallResultInPlace", GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string tree = names.Of("LeakTree");
        string at = names.Of("LeakAt");
        string leaf = names.Of("LeakLeaf");
        string call = names.Of("LeakCall");
        string branch = names.Of("LeakIf");
        string firstSome = names.Of("firstSome");
        string unspan = names.Of("unspan");
        string ordinal = names.Of("ordinal");
        string passing = names.Of("passing");
        string input = names.Coin("call-input")
            ? $"{call}({leaf}(\"go\"), {leaf}(\"y\"))"
            : $"{branch}({leaf}(\"c\"), {leaf}(\"d\"), {leaf}(\"e\"))";
        return $$"""
            type {{tree}} =
                | {{at}}({{tree}})
                | {{leaf}}(Str)
                | {{call}}({{tree}}, {{tree}})
                | {{branch}}({{tree}}, {{tree}}, {{tree}})

            let {{firstSome}} (first: Maybe(Int)) (second: Maybe(Int)) =
                match first with
                    | Some(_found) -> first
                    | None -> second

            let recursive {{unspan}} (t: {{tree}}) =
                match t with
                    | {{at}}(inner) -> {{unspan}}(inner)
                    | other -> other

            let recursive {{ordinal}} (function: {{tree}}) (self: Str) (n: Int) =
                match function with
                    | {{call}}(inner, _a) -> {{ordinal}}(inner)(self)(n + 1)
                    | {{leaf}}(name) ->
                        if name == self
                        then Some(n)
                        else None
                    | _ -> None

            let recursive {{passing}} (expression: {{tree}}) (name: Str) (self: Str) =
                match expression with
                    | {{call}}(function, argument) ->
                        self
                        |> {{passing}}(argument)(name)
                        |> {{firstSome}}({{passing}}(function)(name)(self))
                        |> {{firstSome}}(match {{unspan}}(argument) with
                            | {{leaf}}(passed) ->
                                if passed == name
                                then {{ordinal}}(function)(self)(0)
                                else None
                            | _ -> None)
                    | {{branch}}(c, t, e) ->
                        self
                        |> {{passing}}(e)(name)
                        |> {{firstSome}}({{passing}}(t)(name)(self))
                        |> {{firstSome}}({{passing}}(c)(name)(self))
                    | _ -> None

            let {{names.Entry}} (seed: Int) =
                match {{passing}}({{input}})("y")("go") with
                    | None -> seed
                    | Some(found) -> seed + found
            """;
    }
}
