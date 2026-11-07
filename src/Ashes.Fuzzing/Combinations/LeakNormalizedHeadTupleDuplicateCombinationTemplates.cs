using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop collecting <c>(f.label, n)</c> pairs onto a reference-counted accumulator, the way stage 1's
/// optimizer groups capture sites and association entries. The pair is a tuple literal placed in the
/// arena, holding the duplicate its pattern owner <c>f</c> took for the label; the reference-counted
/// cell normalizes the tuple into a copy that retains the label itself, and the arena tuple's
/// duplicate was released by nobody: one string per pair.
/// </summary>
internal sealed class NormalizedHeadTupleDuplicateTemplate() : LeakCombinationTemplate(
    "leak.normalized-head-tuple-duplicate", "normalizedHeadTupleDuplicate", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string fn = names.Of("Fn");
        string collect = names.Of("collectGroups");
        string walk = names.Of("walk");
        int width = names.Between(1, 3);
        string first = names.Word();
        string word = names.Pick("ash", "ember", "coal", "flint");
        string items = "[" + string.Join(", ", Enumerable.Range(0, width).Select(i => i == 0
            ? $"{fn}(label = {names.Word()}, body = [seed])"
            : $"{fn}(label = \"{word}{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}\", body = [])")) + "]";
        return $$"""
            type {{fn}} =
                | label: Str
                | body: List(Int)

            let recursive {{collect}} (fns: List({{fn}})) acc =
                match fns with
                    | [] -> acc
                    | f :: rest -> {{collect}}(rest)((f.label, Ashes.Text.byteLength(f.label)) :: acc)

            let recursive {{walk}} groups (n: Int) =
                match groups with
                    | [] -> n
                    | (label, index) :: tail -> {{walk}}(tail)(n + index + Ashes.Text.byteLength(label))

            let {{names.Entry}} (seed: Int) =
                (let all = {{fn}}(label = {{first}}, body = []) :: {{items}}
                in {{walk}}({{collect}}(all)([]))(seed))
            """;
    }
}
