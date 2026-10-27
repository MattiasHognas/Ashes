using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A parser's top-level <c>let</c> (the self-hosted parser's <c>parserParseTopLevelLet</c>): the
/// <c>in</c> arm builds its result tuple under a <c>let</c>, the other arm is a join already
/// normalized (another such tuple beside a call of the mutually recursive item loop). The outer join
/// owns both arms' references, so the function's return takes its value over instead of retaining
/// it a second time, for a list of records and a list of integers alike.
/// </summary>
internal sealed class FreshTupleArmBesideNormalizedJoinTemplate() : LeakCombinationTemplate(
    "leak.fresh-tuple-arm-beside-normalized-join", "freshTupleArmJoin", GeneratedFeature.Adt, GeneratedFeature.MutualRecursion, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string token = names.Of("LeakToken");
        string expr = names.Of("LeakExpr");
        string number = names.Of("LeakNumber");
        string bound = names.Of("LeakBound");
        string advance = names.Of("advanceToken");
        string isKind = names.Of("isTokenKind");
        string expression = names.Of("parseValue");
        string items = names.Of("programItems");
        string top = names.Of("topLevelLet");
        string binding = names.Of("topLevelBinding");
        bool records = names.Coin("records");
        string itemType = records ? names.Of("LeakItem") : "Int";
        string itemDeclaration = records ? $"type {itemType} =\n    | {names.Of("LeakTopLet")}(Str, {expr})\n\n" : "";
        string pushed = records ? $"{names.Of("LeakTopLet")}(name)(value)" : "letToken.position";
        string word = names.Word();
        int source = names.Between(1, 9);
        return $$"""
            type {{token}} =
                | kind: Int
                | text: Str
                | position: Int

            type {{expr}} =
                | {{number}}(Int)
                | {{bound}}(Str, {{expr}}, {{expr}})

            {{itemDeclaration}}let {{advance}} (state: (List({{token}}), Str)) =
                match state with
                    | (head :: tail, source) -> (head, (tail, source))
                    | ([], _source) -> ({{token}}(kind = 0, text = "", position = 0), state)

            let {{isKind}} (kind: Int) (state: (List({{token}}), Str)) =
                match state with
                    | (head :: _, _source) -> head.kind == kind
                    | ([], _source) -> kind == 0

            let {{expression}} (state: (List({{token}}), Str)) =
                match {{advance}}(state) with
                    | (head, after) -> ({{number}}(head.position), after)

            let recursive {{items}} (reversedItems: List({{itemType}})) (state: (List({{token}}), Str)) =
                if {{isKind}}(1)(state)
                then {{top}}(reversedItems)(state)
                else (Ashes.Collection.List.reverse(reversedItems), None, state)
            and {{top}} reversedItems state =
                match {{advance}}(state) with
                    | (letToken, afterLet) ->
                        match {{binding}}(afterLet) with
                            | (name, value, afterBinding) ->
                                if {{isKind}}(3)(afterBinding)
                                then
                                    match {{expression}}(afterBinding) with
                                        | (body, afterBody) ->
                                            let found = {{bound}}(name)(value)(body)
                                            in (Ashes.Collection.List.reverse(reversedItems), Some(found), afterBody)
                                else
                                    if letToken.position < 0
                                    then
                                        let missing = {{bound}}(name)(value)({{number}}(0))
                                        in (Ashes.Collection.List.reverse(reversedItems), Some(missing), afterBinding)
                                    else {{items}}({{pushed}} :: reversedItems)(afterBinding)
            and {{binding}} state =
                match {{advance}}(state) with
                    | (name, afterName) ->
                        match {{expression}}(afterName) with
                            | (value, afterValue) -> (name.text, value, afterValue)

            let {{names.Entry}} (seed: Int) =
                (let tokens = [{{token}}(kind = 1, text = "let", position = 1), {{token}}(kind = 2, text = {{word}} + Ashes.Text.fromInt(seed), position = 2), {{token}}(kind = 4, text = "1", position = {{source}}), {{token}}(kind = 1, text = "let", position = 4), {{token}}(kind = 2, text = "g", position = 5), {{token}}(kind = if seed % 2 == 0 then 3 else 4, text = "2", position = 6), {{token}}(kind = 4, text = "4", position = 7)]
                in
                    match {{items}}([])((tokens, "source")) with
                        | (found, body, _) ->
                            Ashes.Collection.List.length(found) + (match body with
                                | Some(_) -> 1
                                | None -> 0))
            """;
    }
}
