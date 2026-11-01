using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop that calls a function parameter for an optional key and matches on it, binding the key
/// in one arm. The function behind the parameter reads the key out of a record a call returned, or
/// builds a fresh `None`, so it normalizes its result onto the reference-counted heap; the call
/// through the parameter asks for an arena result instead, which the callee copies before releasing
/// its own.
/// </summary>
internal sealed class UnknownCalleeVariantResultTemplate() : LeakCombinationTemplate(
    "leak.unknown-callee-variant-result", "unknownCalleeVariant", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string layout = names.Of("LeakLayout");
        string layoutOf = names.Of("layoutOf");
        string keyOf = names.Of("keyOf");
        string countKeys = names.Of("countKeys");
        string word = names.Word();
        string keyFunction = names.Coin("lambda") ? $"(given (n: Int) -> {keyOf}(n))" : keyOf;
        string someArm = names.Coin("guarded")
            ? $$"""
                            | Some(key) ->
                                if Ashes.Text.length(key) > 40
                                then total
                                else {{countKeys}}(rest)(groupKey)(total + Ashes.Text.length(key))
            """
            : $"                | Some(key) -> {countKeys}(rest)(groupKey)(total + Ashes.Text.length(key))";
        return $$"""
            type {{layout}} =
                | layoutName: Maybe(Str)
                | layoutArity: Int

            let {{layoutOf}} (n: Int) =
                if n % 3 == 0
                then None
                else
                    if n % 3 == 1
                    then Some({{layout}}(layoutName = Some({{word}} + Ashes.Text.fromInt(n)), layoutArity = n))
                    else Some({{layout}}(layoutName = None, layoutArity = n))

            let {{keyOf}} (n: Int) =
                match {{layoutOf}}(n) with
                    | Some(layout) -> layout.layoutName
                    | None -> None

            let recursive {{countKeys}} (candidates: List(Int)) (groupKey: Int -> Maybe(Str)) (total: Int) =
                match candidates with
                    | [] -> total
                    | arm :: rest ->
                        match groupKey(arm) with
            {{someArm}}
                            | None -> {{countKeys}}(rest)(groupKey)(total + 1)

            let {{names.Entry}} (seed: Int) = {{countKeys}}({{names.Ints(names.Between(3, 7), 0, 20)}})({{keyFunction}})(seed)
            """;
    }
}

/// <summary>
/// A list piped into a lambda applied where it is written, whose result keeps some of the list's
/// heads. The lambda's compiled body returns a reference-counted list, so the consumed list is
/// released with its heads rather than spine-only.
/// </summary>
internal sealed class AppliedLambdaConsumedListTemplate() : LeakCombinationTemplate(
    "leak.applied-lambda-consumed-list", "appliedLambdaList", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string reference = names.Of("LeakReference");
        string modulesOf = names.Of("modulesOf");
        string dropFirst = names.Of("dropFirst");
        string mkRefs = names.Of("mkRefs");
        string needed = names.Of("needed");
        string rounds = names.Of("rounds");
        string word = names.Word();
        string other = names.Word();
        return $$"""
            type {{reference}} =
                | referenceModule: Str
                | referenceMember: Str

            let recursive {{modulesOf}} (references: List({{reference}})) =
                match references with
                    | [] -> []
                    | reference :: rest -> reference.referenceModule :: {{modulesOf}}(rest)

            let recursive {{dropFirst}} (names: List(Str)) =
                match names with
                    | [] -> []
                    | name :: rest ->
                        if name == {{word}} + "1"
                        then {{dropFirst}}(rest)
                        else name :: {{dropFirst}}(rest)

            let {{mkRefs}} (seed: Int) = [{{reference}}(referenceModule = {{word}} + Ashes.Text.fromInt(seed - seed + 1), referenceMember = {{other}} + "a"), {{reference}}(referenceModule = {{other}} + Ashes.Text.fromInt(seed), referenceMember = {{word}} + "b")]

            let {{needed}} (seed: Int) =
                seed
                |> {{mkRefs}}
                |> {{modulesOf}}
                |> (given (names) -> {{dropFirst}}(names))

            let {{names.Entry}} (seed: Int) = seed + Ashes.Collection.List.length({{needed}}(seed))

            let recursive {{rounds}} (n: Int) (total: Int) =
                if n == 0
                then total
                else {{rounds}}(n - 1)(total + {{names.Entry}}({{names.Between(1, 9)}}))
            """;
    }

    // The entry runs from a plain counting loop: the census's own loop, which binds the entry's
    // result and renders it, lowers the pipe without the spine-only release.
    protected override string? TrailingWorkload(LeakNames names) =>
        $"{names.Of("rounds")}(ROUNDS)(0)\n|> Ashes.Text.fromInt\n|> Ashes.IO.print";
}

/// <summary>
/// A list read out of records (module-name strings or integer ids) handed to a named helper, through
/// a pipe or as a nested call, inside a function the counting loop inlines. The helper's entry
/// adopts a handed-over argument, so the closure the inlined body rebuilds for it carries the
/// adoption bit, and the helper takes the fresh list over rather than copying it. The list comes
/// from a reuse specialization over the fresh records, whose self call and rebuilt cells stay in
/// place across its scopes' resets.
/// </summary>
internal sealed class NamedHelperConsumedListTemplate() : LeakCombinationTemplate(
    "leak.named-helper-consumed-list", "namedHelperList", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string reference = names.Of("LeakReference");
        string fieldsOf = names.Of("fieldsOf");
        string dropFirst = names.Of("dropFirst");
        string mkRefs = names.Of("mkRefs");
        string needed = names.Of("needed");
        string count = names.Of("count");
        string rounds = names.Of("rounds");
        string word = names.Word();
        string other = names.Word();
        bool ids = names.Coin("ids");
        string element = ids ? "Int" : "Str";
        string field = ids ? "referenceId" : "referenceModule";
        string dropped = ids ? "1" : $"{word} + \"1\"";
        string neededBody = names.Coin("nested")
            ? $"    {dropFirst}({fieldsOf}({mkRefs}(seed)))"
            : $"""
                  seed
                  |> {mkRefs}
                  |> {fieldsOf}
                  |> {dropFirst}
              """;
        return $$"""
            type {{reference}} =
                | referenceModule: Str
                | referenceId: Int

            let recursive {{fieldsOf}} (references: List({{reference}})) =
                match references with
                    | [] -> []
                    | reference :: rest -> reference.{{field}} :: {{fieldsOf}}(rest)

            let recursive {{dropFirst}} (values: List({{element}})) =
                match values with
                    | [] -> []
                    | value :: rest ->
                        if value == {{dropped}}
                        then {{dropFirst}}(rest)
                        else value :: {{dropFirst}}(rest)

            let {{mkRefs}} (seed: Int) = [{{reference}}(referenceModule = {{word}} + Ashes.Text.fromInt(seed - seed + 1), referenceId = seed - seed + 1), {{reference}}(referenceModule = {{other}} + Ashes.Text.fromInt(seed), referenceId = seed + 1), {{reference}}(referenceModule = {{word}} + "b", referenceId = seed + 2)]

            let {{needed}} (seed: Int) =
            {{neededBody}}

            let recursive {{count}} (values: List({{element}})) (total: Int) =
                match values with
                    | [] -> total
                    | _ :: rest -> {{count}}(rest)(total + 1)

            let {{names.Entry}} (seed: Int) = seed + {{count}}({{needed}}(seed))(0)

            let recursive {{rounds}} (n: Int) (total: Int) =
                if n == 0
                then total
                else {{rounds}}(n - 1)(total + {{names.Entry}}({{names.Between(1, 9)}}))
            """;
    }

    // The entry runs from a plain counting loop, which inlines the helper call into its body.
    protected override string? TrailingWorkload(LeakNames names) =>
        $"{names.Of("rounds")}(ROUNDS)(0)\n|> Ashes.Text.fromInt\n|> Ashes.IO.print";
}
