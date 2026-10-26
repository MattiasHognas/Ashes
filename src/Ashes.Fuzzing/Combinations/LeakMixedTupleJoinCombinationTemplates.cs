using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A match returning a tuple of literals from one arm and a tuple of fresh strings from another
/// (the parser's <c>parserQualifiedName</c>), taken apart by the caller into a constructor that
/// outlives the match: the join owns its tuple, and each element the constructor keeps is a string
/// of its own, not one the tuple releases.
/// </summary>
internal sealed class MixedTupleJoinTemplate() : LeakCombinationTemplate(
    "leak.mixed-tuple-join", "mixedTupleJoin", GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.Call)
{
    protected override string Declarations(LeakNames names)
    {
        string expr = names.Of("LeakName");
        string plain = names.Of("LeakPlain");
        string qualified = names.Of("LeakQualified");
        string split = names.Of("qualifiedParts");
        string build = names.Of("nameOf");
        string size = names.Of("nameSize");
        string word = names.Word();
        string separator = names.Coin("dotted") ? "." : "::";
        return $$"""
            type {{expr}} =
                | {{plain}}(Str)
                | {{qualified}}(Str, Str)

            let {{split}} (parts: List(Str)) =
                match parts with
                    | [] -> ("", "")
                    | name :: moduleParts ->
                        (match moduleParts with
                            | [] -> ""
                            | first :: _ -> first + "{{separator}}", name)

            let {{build}} (parts: List(Str)) =
                match {{split}}(parts) with
                    | (moduleName, name) ->
                        if moduleName == ""
                        then {{plain}}(name)
                        else {{qualified}}(moduleName)(name)

            let {{size}} (value: {{expr}}) =
                match value with
                    | {{plain}}(name) -> Ashes.Text.length(name)
                    | {{qualified}}(moduleName, name) -> Ashes.Text.length(moduleName) + Ashes.Text.length(name)

            let {{names.Entry}} (seed: Int) =
                {{size}}({{build}}([{{word}} + Ashes.Text.fromInt(seed), "m" + Ashes.Text.fromInt(seed + {{names.Between(1, 9)}})]))
            """;
    }
}

/// <summary>
/// Two mutually recursive functions returning a tuple whose list is a call's fresh result (the
/// parser's item loop): the caller asks for the tuple's parts, and a part that is an owned slot's
/// own value takes the slot's reference with it.
/// </summary>
internal sealed class MutualRecursionTuplePartTemplate() : LeakCombinationTemplate(
    "leak.mutual-recursion-tuple-part", "mutualRecursionTuplePart", GeneratedFeature.Record, GeneratedFeature.MutualRecursion, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string item = names.Of("LeakItem");
        string items = names.Of("itemsOf");
        string top = names.Of("topItem");
        string word = names.Word();
        string keyword = names.Coin("let") ? "let" : "def";
        return $$"""
            type {{item}} =
                | name: Str
                | size: Int

            let recursive {{items}} (reversed: List({{item}})) (state: (List(Str), Bool)) =
                match state with
                    | ([], flag) -> (Ashes.Collection.List.reverse(reversed), state)
                    | (token :: rest, flag) ->
                        if token == "{{keyword}}"
                        then {{top}}(reversed)((rest, flag))
                        else (Ashes.Collection.List.reverse(reversed), (rest, flag))
            and {{top}} (reversed: List({{item}})) (state: (List(Str), Bool)) =
                match state with
                    | ([], flag) -> (Ashes.Collection.List.reverse(reversed), state)
                    | (name :: rest, flag) -> {{items}}({{item}}(name = {{word}} + " " + name, size = {{names.Between(1, 9)}}) :: reversed)((rest, flag))

            let {{names.Entry}} (seed: Int) =
                match {{items}}([])((["{{keyword}}", "x" + Ashes.Text.fromInt(seed), "y"], false)) with
                    | (found, (rest, _flag)) -> Ashes.Collection.List.length(found) + Ashes.Collection.List.length(rest)
            """;
    }
}
