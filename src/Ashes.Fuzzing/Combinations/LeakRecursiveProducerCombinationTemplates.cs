using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A recursive list producer matching a tuple literal of its list parameter
/// (<c>match (items, 0) with | (entry :: rest, _) -&gt; entry :: keep(rest)</c>): the head bound at
/// the list's position descends from that parameter as in a match on the list alone, so the cell is
/// reference-counted and the whole result is released by its caller.
/// </summary>
internal sealed class ProducerOverTupleScrutineeTemplate() : LeakCombinationTemplate(
    "leak.producer-over-tuple-scrutinee", "producerOverTupleScrutinee", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string collect = names.Of("collectWords");
        string keep = names.Of("keepOverTuple");
        string other = names.Pick("0", "seed", "\"tag\"");
        string otherType = string.Equals(other, "\"tag\"", StringComparison.Ordinal) ? "Str" : "Int";
        string rest = names.Pick("_", "_other");
        return $$"""
            let recursive {{collect}} (n: Int) (acc: List(Str)) =
                if n <= 0
                then acc
                else {{collect}}(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive {{keep}} (other: {{otherType}}) (items: List(Str)) =
                match (items, other) with
                    | (entry :: rest, {{rest}}) -> entry :: {{keep}}(other)(rest)
                    | _ -> []

            let {{names.Entry}} (seed: Int) = Ashes.Collection.List.length({{keep}}({{other}})({{collect}}({{names.Between(2, 6)}})([]))) + seed
            """;
    }
}
