using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A non-tail recursive producer consing a tuple of a match-bound string onto its own result
/// (`(name, base) :: blocks(base + 1)(rest)`). The tuple is reference-counted and stores an owned
/// copy of the string; built in the arena instead, it holds a retain of the string that the cell's
/// copy of the tuple retains again, and the first retain is never released.
/// </summary>
internal sealed class PatternStringTupleHeadTemplate() : LeakCombinationTemplate(
    "leak.pattern-string-tuple-head", "patternStringTupleHead", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string blocks = names.Of("blocks");
        string size = names.Of("size");
        string labels = names.Of("labels");
        string prefix = names.Pick("end_", "shared_", "loop_", "next_");
        int count = names.Between(1, 4);
        bool stringFirst = names.Coin("string-first");
        string tupleType = stringFirst ? "(Str, Int)" : "(Int, Str)";
        string pair = stringFirst ? "(name, base)" : "(base, name)";
        string pattern = stringFirst ? "(_name, b)" : "(b, _name)";
        return $$"""
            let recursive {{blocks}} (base: Int) (names: List(Str)) =
                match names with
                    | [] -> []
                    | name :: rest -> {{pair}} :: {{blocks}}(base + 1)(rest)

            let recursive {{size}} (entries: List({{tupleType}})) (n: Int) =
                match entries with
                    | [] -> n
                    | {{pattern}} :: rest -> {{size}}(rest)(n + b)

            let recursive {{labels}} (seed: Int) (k: Int) (acc: List(Str)) =
                if k == 0
                then acc
                else {{labels}}(seed)(k - 1)(("{{prefix}}" + Ashes.Text.fromInt(seed + k)) :: acc)

            let {{names.Entry}} (seed: Int) = {{size}}({{blocks}}(seed)({{labels}}(seed)({{count}})([])))(0)
            """;
    }
}
