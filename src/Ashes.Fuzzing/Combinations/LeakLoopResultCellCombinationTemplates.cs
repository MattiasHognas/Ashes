using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over a table of named entries returns <c>Some</c> of the entry it finds, a value a pattern
/// binds out of the loop's list parameter. The loop releases its parameter at the exit, so the
/// result takes a reference to the entry, which only a cell on the reference-counted heap
/// releases. A helper may look the entry up and rebuild the <c>Some</c> around it, taking a
/// reference of its own out of the owned call result.
/// </summary>
internal sealed class LoopResultCellHoldingPatternBindingTemplate() : LeakCombinationTemplate(
    "leak.loop-result-cell-holding-pattern-binding", "loopResultCell", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string lookup = names.Of("lookupEntry");
        string handOff = names.Of("handOffEntry");
        string build = names.Of("buildTable");
        string count = names.Of("countFound");
        string word = names.Word();
        bool stringEntries = names.Coin("string-entries");
        string entryType = stringEntries ? "Str" : "List(Int)";
        string entryValue = stringEntries ? $$"""{{word}} + Ashes.Text.fromInt(n)""" : "[n, n + 1]";
        string measure = stringEntries ? "Ashes.Text.byteLength(entry)" : "Ashes.Collection.List.length(entry)";
        string found = names.Coin("through-helper") ? $$"""{{handOff}}(table)(key)""" : $$"""{{lookup}}(key)(table)""";
        return $$"""
            let recursive {{lookup}} (key: Str) (table: List((Str, {{entryType}}))) =
                match table with
                    | [] -> None
                    | (name, entry) :: rest ->
                        if name == key
                        then Some(entry)
                        else {{lookup}}(key)(rest)

            let {{handOff}} (table: List((Str, {{entryType}}))) (key: Str) =
                match {{lookup}}(key)(table) with
                    | Some(entry) -> Some(entry)
                    | None -> None

            let recursive {{build}} (n: Int) (acc: List((Str, {{entryType}}))) =
                if n == 0
                then acc
                else {{build}}(n - 1)(("k" + Ashes.Text.fromInt(n), {{entryValue}}) :: acc)

            let {{count}} (table: List((Str, {{entryType}}))) (key: Str) =
                match {{found}} with
                    | Some(entry) -> {{measure}}
                    | None -> 0

            let {{names.Entry}} (seed: Int) = {{count}}({{build}}(seed + 2)([]))("k1") + seed
            """;
    }
}
