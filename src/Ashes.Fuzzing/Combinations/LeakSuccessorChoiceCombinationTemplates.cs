using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop that grows a list held in a record parameter by a cons written as a field of a record
/// literal successor (<c>walk(rest)(State(seen = name :: state.seen, count = state.count))</c>).
/// The back edge copies the arena record, string heads copied outright and other parts retained by
/// the copy; the references the arena cell took for its head and tail were never released.
/// </summary>
internal sealed class RecordLiteralFieldConsTemplate() : LeakCombinationTemplate(
    "leak.record-literal-field-cons", "recordLiteralFieldCons", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string item = names.Of("LeakItem");
        string state = names.Of("LeakState");
        string build = names.Of("build");
        string walk = names.Of("walk");
        bool adt = names.Coin("adtElement");
        string element = adt ? item : "Str";
        string fieldValue = names.Coin("tailField") ? "name :: rest" : "name :: state.seen";
        string count = names.Coin("countField") ? "state.count + 1" : "state.count";
        string current = names.Coin("currentRead") ? "state.current" : "None";
        string first = adt ? $"{item}(\"coal\" + Ashes.Text.fromInt(n))" : "\"coal\" + Ashes.Text.fromInt(n)";
        string second = adt ? $"{item}(\"ember{LeakNames.Text(names.Between(1, 9))}\")" : $"\"ember\" + Ashes.Text.fromInt(n + {LeakNames.Text(names.Between(1, 9))})";
        return $$"""
            type {{item}} =
                | {{item}}(Str)

            type {{state}} =
                | seen: List({{element}})
                | count: Int
                | current: Maybe(Int)

            let recursive {{walk}} (items: List({{element}})) (state: {{state}}) =
                match items with
                    | [] -> Ashes.Collection.List.length(state.seen) + state.count
                    | name :: rest -> {{walk}}(rest)({{state}}(seen = {{fieldValue}}, count = {{count}}, current = {{current}}))

            let {{build}} (n: Int) = [{{first}}, {{second}}]

            let {{names.Entry}} (seed: Int) = {{walk}}({{build}}(seed))({{state}}(seen = [], count = seed, current = None))
            """;
    }
}
