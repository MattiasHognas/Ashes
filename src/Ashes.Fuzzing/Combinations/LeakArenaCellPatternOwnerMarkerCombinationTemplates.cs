using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over a list it normalizes onto the reference-counted heap at its entry, returning
/// <c>Some(head)</c> of a record element, the way stage 1's <c>listAt</c> serves
/// <c>blockSuccessorsOfIndex</c>. The arena <c>Some</c> keeps the retain its pattern owner took for
/// the head, which is still a marker while the cell is built; parked in no owned slot, every call
/// left the record copy and its list behind.
/// </summary>
internal sealed class ArenaCellPatternOwnerMarkerTemplate() : LeakCombinationTemplate(
    "leak.arena-cell-pattern-owner-marker", "arenaCellOwnerMarker", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string block = names.Of("Block");
        string listAt = names.Of("listAt");
        string successorsOf = names.Of("successorsOf");
        string total = names.Of("total");
        int width = names.Between(2, 4);
        int index = names.Between(0, width - 1);
        string blocks = "[" + string.Join(", ", Enumerable.Range(0, width).Select(i =>
            $"{block}(start = {i.ToString(System.Globalization.CultureInfo.InvariantCulture)}, successors = [seed, {(i + names.Between(1, 3)).ToString(System.Globalization.CultureInfo.InvariantCulture)}])")) + "]";
        return $$"""
            type {{block}} =
                | start: Int
                | successors: List(Int)

            let recursive {{listAt}} (items: List({{block}})) (index: Int) =
                match items with
                    | [] -> None
                    | head :: rest ->
                        if index == 0
                        then Some(head)
                        else {{listAt}}(rest)(index - 1)

            let {{successorsOf}} (blocks: List({{block}})) (index: Int) =
                match {{listAt}}(blocks)(index) with
                    | Some({{block}} { successors = successors }) -> successors
                    | None -> []

            let recursive {{total}} (xs: List(Int)) =
                match xs with
                    | [] -> 0
                    | x :: rest -> x + {{total}}(rest)

            let {{names.Entry}} (seed: Int) =
                (let blocks = {{blocks}}
                in {{total}}({{successorsOf}}(blocks)({{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}})))
            """;
    }
}
