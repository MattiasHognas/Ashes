using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function returns a list literal whose later element lands on the reference-counted heap (a
/// string built at run time, a constructor of scalars) and whose earlier element would stay in the
/// arena (a static string, a constructor over one). The earlier cell is then the only holder of the
/// later one, and the arena is reclaimed without releasing it, unless that head is copied onto the
/// reference-counted heap so its cell follows.
/// </summary>
internal sealed class LiteralArenaCellOverReferenceCountedCellTemplate() : LeakCombinationTemplate(
    "leak.literal-arena-cell-over-rc-cell", "literalArenaCell", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("LabelKind");
        string named = names.Of("Named");
        string plain = names.Of("Plain");
        string produce = names.Of("produceLabels");
        string walk = names.Of("walkLabels");
        string run = names.Of("countLabels");
        string word = names.Word();
        int variant = names.Variant("element", 3);
        string elementType = variant == 2 ? kind : variant == 1 ? "(Str, Int)" : "Str";
        string literal = variant switch
        {
            2 => $$"""[{{named}}({{word}}), {{plain}}(seed), {{named}}({{word}})]""",
            1 => $$"""[({{word}}, 1), ({{word}} + Ashes.Text.fromInt(seed), 2)]""",
            _ => $$"""[{{word}}, {{word}} + Ashes.Text.fromInt(seed), {{word}}]""",
        };
        return $$"""
            type {{kind}} =
                | {{named}}(Str)
                | {{plain}}(Int)

            let {{produce}} (seed: Int) = {{literal}}

            let recursive {{walk}} (items: List({{elementType}})) (count: Int) =
                match items with
                    | [] -> count
                    | _ :: rest -> {{walk}}(rest)(count + 1)

            let recursive {{run}} (seed: Int) = if seed < 0 then {{run}}(seed + 1) else {{walk}}({{produce}}(seed))(0)

            let {{names.Entry}} (seed: Int) = {{run}}(seed) + seed
            """;
    }
}
