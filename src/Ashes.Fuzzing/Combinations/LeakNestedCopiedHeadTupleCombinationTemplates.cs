using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop whose one arm passes its list accumulator through unchanged while the other conses a
/// head tuple holding a nested tuple over a pattern-bound heap value
/// (<c>((label, index), 0) :: known</c>). The accumulator's mixed shape keeps the new cell in the
/// arena, and the back edge copies it with references of its own; the nested tuple's duplicate of
/// the pattern owner was never released, one per consed entry.
/// </summary>
internal sealed class NestedCopiedHeadTupleTemplate() : LeakCombinationTemplate(
    "leak.nested-copied-head-tuple", "nestedHeadTuple", GeneratedFeature.Tuple, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    protected override string Declarations(LeakNames names)
    {
        string entry = names.Of("LeakGroupEntry");
        string loop = names.Of("groupLoop");
        bool records = names.Coin("recordElement");
        bool nestedFirst = names.Coin("nestedFirst");
        bool deeper = names.Coin("deeper");
        bool sibling = names.Coin("siblingList");
        string word = names.Word();
        string elementType = records ? entry : "Str";
        string element = records ? $"{entry}(label = Ashes.Text.fromInt(n) + {word}, at = n)" : $"Ashes.Text.fromInt(n) + {word}";
        string innerType = deeper ? $"(({elementType}, Int), Int)" : $"({elementType}, Int)";
        string inner = deeper ? "((label, index), index)" : "(label, index)";
        string headType = nestedFirst ? $"({innerType}, Int)" : $"(Int, {innerType})";
        string head = nestedFirst ? $"({inner}, index + 1)" : $"(index + 1, {inner})";
        string siblingParameter = sibling ? $" (conflicting: List(({elementType}, Int)))" : string.Empty;
        string siblingPass = sibling ? "(conflicting)" : string.Empty;
        string siblingPush = sibling ? "((label, index) :: conflicting)" : string.Empty;
        string exit = sibling ? "(known, conflicting)" : "known";
        string call = $"{loop}(groups)([])";
        string result = sibling
            ? $"match {call}([]) with\n            | (built, _) -> Ashes.Collection.List.length(built) + seed"
            : $"Ashes.Collection.List.length({call}) + seed";
        int skipped = names.Between(1, 3);
        string groups = names.Coin("mappedGroups")
            ? $"Ashes.Collection.List.map(given (n) -> ({element}, n))(seed :: {names.Ints(names.Between(2, 5), 0, 4)})"
            : LiteralGroups(names, records, entry);
        return $$"""
            type {{entry}} =
                | label: Str
                | at: Int

            let recursive {{loop}} (groups: List(({{elementType}}, Int))) (known: List({{headType}})){{siblingParameter}} =
                match groups with
                    | [] -> {{exit}}
                    | (label, index) :: tail ->
                        if index == {{skipped}}
                        then {{loop}}(tail)(known){{siblingPush}}
                        else {{loop}}(tail)({{head}} :: known){{siblingPass}}

            let {{names.Entry}} (seed: Int) =
                let groups = {{groups}}
                in
                    {{result}}
            """;
    }

    // A literal list of groups, its strings static until the loop's entry copies them.
    private static string LiteralGroups(LeakNames names, bool records, string entry)
    {
        IReadOnlyList<string> words = names.DistinctWords(names.Between(2, 5));
        IEnumerable<string> groups = words.Select((word, index) => records
            ? $"({entry}(label = {word}, at = {index}), {index})"
            : $"({word}, {index})");
        return $"({(records ? $"{entry}(label = \"seed\", at = seed)" : "\"seed\"")}, seed) :: [{string.Join(", ", groups)}]";
    }
}
