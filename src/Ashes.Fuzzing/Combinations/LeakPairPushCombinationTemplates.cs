using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// The linker's patch loop: each step pushes a pattern-bound buffer and a fresh slice from a helper
/// at once onto its accumulator. Both cells are reference-counted, so the spine is never copied and
/// the caller releases the whole list.
/// </summary>
internal sealed class PairPushedOntoLoopAccumulatorTemplate() : LeakCombinationTemplate(
    "leak.pair-pushed-onto-loop-accumulator", "pairPushedOntoLoopAccumulator", GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string patch = names.Of("LeakPatch");
        string slice = names.Of("leakSlice");
        string pieces = names.Of("leakPieces");
        string measure = names.Of("leakMeasure");
        int first = 2 + names.Variant("first offset", 4);
        int second = 10 + names.Variant("second offset", 4);
        return $$"""
            type {{patch}} =
                | leakOffset: Int
                | leakBytes: Bytes

            let {{slice}} (bytes: Bytes) (start: Int) (length: Int) =
                length
                |> Ashes.Byte.subText(bytes)(start)
                |> Ashes.Byte.fromText

            let recursive {{pieces}} (bytes: Bytes) (patches: List({{patch}})) (cursor: Int) (acc: List(Bytes)) =
                match patches with
                    | [] -> Ashes.Collection.List.reverse({{slice}}(bytes)(cursor)(Ashes.Byte.length(bytes) - cursor) :: acc)
                    | {{patch}} { leakOffset = offset, leakBytes = replacement } :: rest -> {{pieces}}(bytes)(rest)(offset + Ashes.Byte.length(replacement))(replacement :: {{slice}}(bytes)(cursor)(offset - cursor) :: acc)

            let recursive {{measure}} (values: List(Bytes)) (total: Int) =
                match values with
                    | [] -> total
                    | piece :: rest -> {{measure}}(rest)(total + Ashes.Byte.length(piece))

            let {{names.Entry}} (seed: Int) = {{measure}}({{pieces}}(Ashes.Byte.allocate(40))([{{patch}}(leakOffset = {{first}}, leakBytes = Ashes.Byte.allocate(4)), {{patch}}(leakOffset = {{second}}, leakBytes = Ashes.Byte.allocate(4)), {{patch}}(leakOffset = 25, leakBytes = Ashes.Byte.allocate(seed % 4 + 1))])(0)([]))(0)
            """;
    }
}
