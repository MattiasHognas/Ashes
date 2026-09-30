using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A call's owned result consed straight onto a choice accumulator (<c>walk(rest)(join(p) :: seen)</c>
/// beside <c>walk(rest)(seen)</c>). The back edge copies the successor with references of its own
/// and the cell only borrows its head, so the result needs an owner of its own, released once the
/// successor holds its references, as a <c>let</c> binding of it would give it.
/// </summary>
internal sealed class CallResultOntoChoiceAccumulatorTemplate() : LeakCombinationTemplate(
    "leak.call-result-onto-choice-accumulator", "callResultChoice", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string walk = names.Of("walkNames");
        string word = names.Word();
        string produced = names.Variant("producer", 3) switch
        {
            0 => $"Ashes.Text.join(\"/\")([name, {word}])",
            1 => "Ashes.Text.join(\"\")([name, name])",
            _ => $"Ashes.Text.join(\",\")([{word}, name, Ashes.Text.fromInt(n)])",
        };
        return $$"""
            let recursive {{walk}} (queue: List(Str)) (seen: List(Str)) (n: Int) =
                match queue with
                    | [] -> n + Ashes.Collection.List.length(seen)
                    | name :: rest ->
                        if n > 1
                        then {{walk}}(rest)({{produced}} :: seen)(n + 1)
                        else {{walk}}(rest)(seen)(n + 1)

            let {{names.Entry}} (seed: Int) = {{walk}}([{{word}}, {{word}} + Ashes.Text.fromInt(seed), "a", "b", "c"])([])(0) + seed
            """;
    }
}
