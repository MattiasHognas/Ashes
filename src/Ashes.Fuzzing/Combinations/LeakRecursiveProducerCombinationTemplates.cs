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

/// <summary>
/// A curried recursive list producer whose elements are tuples taken out of its parameter
/// (<c>entry :: keep(other)(rest)</c> over a <c>List((Str, Int))</c>): the head gets an owned
/// reference-counted copy, so the cell holding the reference-counted rest of the result is
/// reference-counted too.
/// </summary>
internal sealed class CurriedProducerOverTupleHeadsTemplate() : LeakCombinationTemplate(
    "leak.curried-producer-over-tuple-heads", "curriedProducerOverTupleHeads", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string collect = names.Of("collectPairs");
        string keep = names.Of("keepPairs");
        string word = names.Word();
        return $$"""
            let recursive {{collect}} (n: Int) (acc: List((Str, Int))) =
                if n <= 0
                then acc
                else {{collect}}(n - 1)((Ashes.Text.fromInt(n), n) :: acc)

            let recursive {{keep}} (marker: Str) (items: List((Str, Int))) =
                match items with
                    | entry :: rest -> entry :: {{keep}}(marker)(rest)
                    | [] -> []

            let {{names.Entry}} (seed: Int) = Ashes.Collection.List.length({{keep}}({{word}})({{collect}}({{names.Between(2, 6)}})([]))) + seed
            """;
    }
}
