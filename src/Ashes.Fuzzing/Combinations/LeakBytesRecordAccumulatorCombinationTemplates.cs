using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop building a list of records that hold a byte buffer, each pushed as a record literal, a
/// let-bound record, a helper's result, or a curried constructor call. The cells are
/// reference-counted whatever the head, so the accumulator is placed on the reference-counted heap
/// with them and the loop hands its list over instead of retaining it.
/// </summary>
internal sealed class BytesRecordAccumulatorTemplate() : LeakCombinationTemplate(
    "leak.bytes-record-accumulator", "bytesRecordAccumulator", GeneratedFeature.TailCall, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string patch = names.Of("LeakPatch");
        string make = names.Of("leakMakePatch");
        string build = names.Of("leakPatches");
        string size = LeakNames.Text(names.Between(1, 9));
        string pushed = names.Variant("head", 4) switch
        {
            0 => $"{build}(index + 1)(count)({patch}(leakOffset = index, leakBytes = Ashes.Byte.allocate({size})) :: acc)",
            1 => $"let patch = {patch}(leakOffset = index, leakBytes = Ashes.Byte.allocate({size})) in {build}(index + 1)(count)(patch :: acc)",
            2 => $"{build}(index + 1)(count)({make}(index) :: acc)",
            _ => $"{build}(index + 1)(count)({patch}(index)(Ashes.Byte.allocate({size})) :: acc)",
        };
        return $$"""
            type {{patch}} =
                | leakOffset: Int
                | leakBytes: Bytes

            let {{make}} (index: Int) = {{patch}}(leakOffset = index, leakBytes = Ashes.Byte.allocate({{size}}))

            let recursive {{build}} (index: Int) (count: Int) (acc: List({{patch}})) =
                if index >= count
                then Ashes.Collection.List.reverse(acc)
                else {{pushed}}

            let {{names.Entry}} (seed: Int) = Ashes.Collection.List.length({{build}}(0)(seed % 5 + 3)([])) + seed
            """;
    }
}
