using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop that replaces its list parameter with a one-element literal whose element, a tuple or a
/// record, holds a sibling list parameter placed on the reference-counted heap
/// (<c>go([(other, [k])])(produce(k))</c>). The back edge copies the literal's arena cells: the
/// element the literal built and the retain its construction took of the sibling were left behind
/// the arena cell, with nothing to release them.
/// </summary>
internal sealed class ListLiteralSuccessorReadingHeapSiblingTemplate() : LeakCombinationTemplate(
    "leak.list-literal-successor-reads-heap-sibling", "listLiteralReadsSibling", GeneratedFeature.TailCall, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string parts = names.Of("Parts");
        string produce = names.Of("produce");
        string loop = names.Of("replace");
        bool record = names.Coin("record");
        string element = record ? parts : "(List(Int), List(Int))";
        string fresh = names.Coin("freshHead") ? "[remaining]" : "[remaining, remaining * 2]";
        string literal = record ? $"[{parts}(front = other, back = {fresh})]" : $"[(other, {fresh})]";
        return $$"""
            type {{parts}} =
                | front: List(Int)
                | back: List(Int)

            let {{produce}} (k: Int) = [k, k + {{LeakNames.Text(names.Between(1, 9))}}]

            let recursive {{loop}} (remaining: Int) (acc: List({{element}})) (other: List(Int)) (total: Int) =
                if remaining == 0
                then total + Ashes.Collection.List.length(acc) + Ashes.Collection.List.length(other)
                else {{loop}}(remaining - 1)({{literal}})({{produce}}(remaining))(total + Ashes.Collection.List.length(other))

            let {{names.Entry}} (seed: Int) = {{loop}}(seed % {{LeakNames.Text(names.Between(3, 7))}} + 2)([])([seed])(seed)
            """;
    }
}
