using System.Text.RegularExpressions;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// The pattern-coverage expansion of stage 1's type inference: a generic loop over match cases expands
/// each pattern through helpers calling the selector-imported <c>append</c> and <c>reverse</c>. The
/// ownership analysis took those imports for unknown callees, so the loop's taken-over record was never
/// proven and every expanded pattern was left behind; they now resolve to the shipped functions.
/// </summary>
internal sealed partial class SelectorImportAliasTemplate() : LeakCombinationTemplate(
    "leak.selector-import-alias", "selectorImportAlias", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    private static readonly string[] Identifiers =
    [
        "TextSpan", "Pattern", "ConstructorInferenceDefinition", "resolveConstructorBinding", "coverageLength",
        "coverageWildcards", "coverageReplaceAt", "coverageFieldIndex", "coveragePlaceRecordFields",
        "coverageExpandPattern", "coverageExpandAlternatives", "coverageCombineChildren", "coverageProduct",
        "coveragePrefixAll", "coverageConsCombinations", "coverageTupleCombinations",
        "coverageConstructorCombinations", "coverageExpandCases", "coverageExpandedArms", "probe", "loop",
    ];

    protected override IReadOnlyList<string> RequiredImports => ["Ashes.Collection.List.append", "Ashes.Collection.List.reverse"];

    protected override string Declarations(LeakNames names)
    {
        string program = Program.Replace("ENTRY", names.Entry, StringComparison.Ordinal)
            .Replace("WIDTH", names.Between(3, 9).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Dictionary<string, string> renamed = new(StringComparer.Ordinal);
        foreach (string identifier in Identifiers)
        {
            renamed[identifier] = names.Of(identifier);
        }

        return IdentifierPattern().Replace(program, match => renamed.TryGetValue(match.Value, out string? name) ? name : match.Value);
    }

    [GeneratedRegex(@"\b[A-Za-z]+\b")]
    private static partial Regex IdentifierPattern();

    private const string Program = """
        type TextSpan =
            | start: Int
            | end: Int

        type Pattern =
            | PatternAt(TextSpan, Pattern)
            | PatternEmptyList
            | PatternVar(Str)
            | PatternWildcard
            | PatternCons(Pattern, Pattern)
            | PatternTuple(List(Pattern))
            | PatternConstructor(Str, List(Pattern))
            | PatternRecord(Str, List((Str, Pattern)))
            | PatternAs(Pattern, Str)
            | PatternOr(List(Pattern))
            | PatternInt(Int)
            | PatternString(Str)
            | PatternRune(Int)
            | PatternBool(Bool)

        type ConstructorInferenceDefinition =
            | name: Str
            | fieldNames: List(Str)

        let recursive resolveConstructorBinding (name: Str) (environment: List(ConstructorInferenceDefinition)) =
            match environment with
                | [] -> None
                | definition :: rest ->
                    if definition.name == name
                    then Some(definition)
                    else resolveConstructorBinding(name)(rest)


        let recursive coverageLength items (acc: Int) =
            match items with
                | [] -> acc
                | _ :: rest -> coverageLength(rest)(acc + 1)

        let recursive coverageWildcards (count: Int) acc =
            if count <= 0
            then acc
            else coverageWildcards(count - 1)(PatternWildcard :: acc)

        let recursive coverageReplaceAt patterns (index: Int) replacement acc =
            match patterns with
                | [] -> reverse(acc)
                | pattern :: rest ->
                    if index == 0
                    then coverageReplaceAt(rest)(index - 1)(replacement)(replacement :: acc)
                    else coverageReplaceAt(rest)(index - 1)(replacement)(pattern :: acc)


        let recursive coverageFieldIndex (fieldName: Str) (fieldNames: List(Str)) (index: Int) =
            match fieldNames with
                | [] -> None
                | candidate :: rest ->
                    if candidate == fieldName
                    then Some(index)
                    else coverageFieldIndex(fieldName)(rest)(index + 1)

        let recursive coveragePlaceRecordFields (fields: List((Str, Pattern))) (fieldNames: List(Str)) positional =
            match fields with
                | [] -> positional
                | (fieldName, fieldPattern) :: rest ->
                    match coverageFieldIndex(fieldName)(fieldNames)(0) with
                        | Some(index) ->
                            []
                            |> coverageReplaceAt(positional)(index)(fieldPattern)
                            |> coveragePlaceRecordFields(rest)(fieldNames)
                        | None -> coveragePlaceRecordFields(rest)(fieldNames)(positional)

        let recursive coverageExpandPattern pattern environment =
            match pattern with
                | PatternAt(_span, inner) -> coverageExpandPattern(inner)(environment)
                | PatternOr(alternatives) -> coverageExpandAlternatives(alternatives)(environment)([])
                | PatternAs(inner, _name) -> coverageExpandPattern(inner)(environment)
                | PatternCons(head, tail) ->
                    coverageConsCombinations(coverageCombineChildren([head, tail])(environment))([])
                | PatternTuple(elements) ->
                    coverageTupleCombinations(coverageCombineChildren(elements)(environment))([])
                | PatternConstructor(name, patterns) ->
                    coverageConstructorCombinations(name)(coverageCombineChildren(patterns)(environment))([])
                | PatternRecord(name, fields) ->
                    match resolveConstructorBinding(name)(environment) with
                        | Some(ConstructorInferenceDefinition { fieldNames = fieldNames }) ->
                            match fieldNames with
                                | [] -> [pattern]
                                | _ ->
                                    let positional =
                                        []
                                        |> coverageWildcards(coverageLength(fieldNames)(0))
                                        |> coveragePlaceRecordFields(fields)(fieldNames)
                                    in
                                        coverageConstructorCombinations(name)(coverageCombineChildren(positional)(environment))([])
                        | None -> [pattern]
                | other -> [other]
        and coverageExpandAlternatives alternatives environment acc =
            match alternatives with
                | [] -> acc
                | alternative :: rest ->
                    environment
                    |> coverageExpandPattern(alternative)
                    |> append(acc)
                    |> coverageExpandAlternatives(rest)(environment)
        and coverageCombineChildren children environment =
            match children with
                | [] -> [[]]
                | child :: rest ->
                    coverageProduct(coverageExpandPattern(child)(environment))(coverageCombineChildren(rest)(environment))([])
        and coverageProduct heads (tails: List(List(Pattern))) acc =
            match heads with
                | [] -> acc
                | head :: rest ->
                    []
                    |> coveragePrefixAll(head)(tails)
                    |> append(acc)
                    |> coverageProduct(rest)(tails)
        and coveragePrefixAll head (tails: List(List(Pattern))) acc =
            match tails with
                | [] -> reverse(acc)
                | tail :: rest -> coveragePrefixAll(head)(rest)((head :: tail) :: acc)
        and coverageConsCombinations (combinations: List(List(Pattern))) acc =
            match combinations with
                | [] -> reverse(acc)
                | (head :: tail :: []) :: rest -> coverageConsCombinations(rest)(PatternCons(head)(tail) :: acc)
                | _ :: rest -> coverageConsCombinations(rest)(acc)
        and coverageTupleCombinations (combinations: List(List(Pattern))) acc =
            match combinations with
                | [] -> reverse(acc)
                | elements :: rest -> coverageTupleCombinations(rest)(PatternTuple(elements) :: acc)
        and coverageConstructorCombinations (name: Str) (combinations: List(List(Pattern))) acc =
            match combinations with
                | [] -> reverse(acc)
                | arguments :: rest -> coverageConstructorCombinations(name)(rest)(PatternConstructor(name)(arguments) :: acc)

        let recursive coverageExpandCases cases environment acc =
            match cases with
                | [] -> reverse(acc)
                | (pattern, body, guard) :: rest ->
                    acc
                    |> coverageExpandedArms(coverageExpandPattern(pattern)(environment))(body)(guard)
                    |> coverageExpandCases(rest)(environment)
        and coverageExpandedArms patterns body guard acc =
            match patterns with
                | [] -> acc
                | pattern :: rest -> coverageExpandedArms(rest)(body)(guard)((pattern, body, guard) :: acc)

        let probe (n: Int) =
            (let environment = [ConstructorInferenceDefinition(name = "Item", fieldNames = ["name", "weight"])]
            in
                let cases = [(PatternEmptyList, n, None), (PatternCons(PatternRecord("Item", [("name", PatternVar("item" + Ashes.Text.fromInt(n % 3)))]), PatternVar("rest")), n + 1, None)]
                in Ashes.Collection.List.length(coverageExpandCases(cases)(environment)([])))

        let recursive loop (n: Int) (acc: Int) =
            if n == 0
            then acc
            else loop(n - 1)(acc + probe(n))

        let ENTRY (seed: Int) = loop(WIDTH)(seed)
        """;
}
