using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A tail-recursive loop called inside a handle conses a value built from a perform's result onto
/// its list accumulator: the perform's string itself or a string joined with it. The loop may run
/// while the handler's one-shot post is pending, so its accumulator gets no reference-counted
/// placement; a counted successor cell consed onto it made the accumulator a counted list returned
/// under an arena result and copied by the caller, leaving the cells and their heads unreleased.
/// </summary>
internal sealed class HandlerContextListAccumulatorTemplate() : LeakCombinationTemplate(
    "leak.handler-context-list-accumulator", "handlerListAccumulator", GeneratedFeature.Capability, GeneratedFeature.Handler, GeneratedFeature.TailCall, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakGather");
        string gather = names.Of("gather");
        string count = names.Of("count");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string performed = $"perform {capability}.get(Unit)";
        string head = names.Coin("joined") ? $"(Ashes.Text.fromInt(i) + {performed})" : performed;
        string arm = names.Coin("post")
            ? $"let x = resume({names.Word()}) in {names.Word()} :: x"
            : $"resume({names.Word()})";
        return $$"""
            capability {{capability}} =
                | get : Unit -> Str

            let recursive {{gather}} (i: Int) (acc: List(Str)) =
                if i == 0
                then acc
                else {{gather}}(i - 1)({{head}} :: acc)

            let recursive {{count}} (xs: List(Str)) (k: Int) =
                match xs with
                    | [] -> k
                    | _ :: rest -> {{count}}(rest)(k + 1)

            let {{probe}} (n: Int) =
                {{count}}(handle {{gather}}({{names.Between(1, 4)}})([Ashes.Text.fromInt(n)]) with
                    | {{capability}}.get(u) -> {{arm}}
                    | return(r) -> r)(0)

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
