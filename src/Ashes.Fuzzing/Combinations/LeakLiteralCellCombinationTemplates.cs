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

/// <summary>
/// A loop's accumulator grows by a cell whose head is a tuple holding a name a pattern binds out of
/// the loop's list parameter. The back edge copies that successor with references of its own, so
/// the tuple borrows the name; a duplicate it took would never be released. The accumulator's type
/// is written out or left to inference, which places the parameter only once the self call's
/// argument types resolve.
/// </summary>
internal sealed class CopiedSuccessorHeadTupleTemplate() : LeakCombinationTemplate(
    "leak.copied-successor-head-tuple", "copiedHeadTuple", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("JumpKind");
        string target = names.Of("Target");
        string other = names.Of("OtherJump");
        string collect = names.Of("collectTargets");
        string total = names.Of("totalCounts");
        string word = names.Word();
        string parameters = names.Coin("annotated")
            ? $$"""(instructions: List({{kind}})) (acc: List((Str, Int)))"""
            : "instructions acc";
        return $$"""
            type {{kind}} =
                | {{target}}(Int, List(Str), Str)
                | {{other}}(Int)

            let recursive {{collect}} {{parameters}} =
                match instructions with
                    | [] -> acc
                    | inst :: tail ->
                        match inst with
                            | {{target}}(_, _, label) -> {{collect}}(tail)((label, 1) :: acc)
                            | _ -> {{collect}}(tail)(acc)

            let recursive {{total}} (entries: List((Str, Int))) (sum: Int) =
                match entries with
                    | [] -> sum
                    | (_, count) :: rest -> {{total}}(rest)(sum + count)

            let {{names.Entry}} (seed: Int) = {{total}}({{collect}}([{{target}}(1)([{{word}}, {{word}} + Ashes.Text.fromInt(seed)])({{word}}), {{other}}(1), {{target}}(2)([])({{word}})])([]))(0) + seed
            """;
    }
}

/// <summary>
/// A list literal's element is a constructor one of whose fields is a list holding a string built at
/// run time. The constructor's cell is built on the reference-counted heap, owning that fresh list:
/// an arena cell would never release it. The list holds one built string, or a static one before it.
/// </summary>
internal sealed class ConstructorOwningFreshChildTemplate() : LeakCombinationTemplate(
    "leak.constructor-owning-fresh-child", "ctorFreshChild", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("JumpKind");
        string target = names.Of("Target");
        string other = names.Of("OtherJump");
        string collect = names.Of("collectTargets");
        string produce = names.Of("produceTargets");
        string total = names.Of("totalCounts");
        string word = names.Word();
        string built = $$"""{{word}} + Ashes.Text.fromInt(seed)""";
        string labels = names.Coin("static-first") ? $$"""[{{word}}, {{built}}]""" : $$"""[{{built}}]""";
        string tail = names.Coin("more-elements") ? $$""", {{other}}(1), {{target}}(2)([])({{word}})""" : "";
        return $$"""
            type {{kind}} =
                | {{target}}(Int, List(Str), Str)
                | {{other}}(Int)

            let recursive {{collect}} (instructions: List({{kind}})) (acc: List((Str, Int))) =
                match instructions with
                    | [] -> acc
                    | inst :: tail ->
                        match inst with
                            | {{target}}(_, _, label) -> {{collect}}(tail)((label, 1) :: acc)
                            | _ -> {{collect}}(tail)(acc)

            let recursive {{total}} (entries: List((Str, Int))) (sum: Int) =
                match entries with
                    | [] -> sum
                    | (_, count) :: rest -> {{total}}(rest)(sum + count)

            let {{produce}} (seed: Int) = [{{target}}(1)({{labels}})({{word}}){{tail}}]

            let {{names.Entry}} (seed: Int) = {{total}}({{collect}}({{produce}}(seed))([]))(0) + seed
            """;
    }
}
