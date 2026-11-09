using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop hands its record parameter on through an `if` that either updates a scalar field or
/// passes the parameter itself. The updated record is an arena cell that retains the unchanged list
/// field it reads out of the parameter, and the join copies that cell, taking a reference of its
/// own, so the cell's retain is never released.
/// </summary>
internal sealed class JoinCopiedUpdateTemplate() : LeakCombinationTemplate(
    "leak.join-copied-record-update", "joinCopiedUpdate", GeneratedFeature.Record, GeneratedFeature.UniqueConstructorUpdate, GeneratedFeature.TailCall, GeneratedFeature.If, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string state = names.Of("JCount");
        string count = names.Of("countNodes");
        string empty = names.Of("emptyLists");
        string run = names.Of("flowWith");
        string loop = names.Of("loop");
        string size = names.Of("size");
        bool strings = names.Coin("str-elements");
        string elementType = strings ? "Str" : "Int";
        string element = strings ? names.Word() : LeakNames.Text(names.Between(1, 9));
        string extraUpdate = names.Coin("update-calls") ? ", calls = count.calls + 1" : "";
        string result = names.Coin("keep-flow")
            ? $"if references == calls\n                        then {size}(flow)(0)\n                        else references"
            : $"{size}(flow)(references)";
        string nodes = "[7, " + names.Ints(names.Between(1, 5), 6, 8)[1..];
        return $$"""
            type {{state}} =
                | references: Int
                | calls: Int
                | flow: List(List({{elementType}}))

            let recursive {{count}} (self: Int) (nodes: List(Int)) (count: {{state}}) =
                match nodes with
                    | [] -> count
                    | name :: rest ->
                        {{count}}(self)(rest)((if name == self
                        then count with references = count.references + 1{{extraUpdate}}
                        else count))

            let recursive {{empty}} (count: Int) =
                if count == 0
                then []
                else [{{element}}] :: {{empty}}(count - 1)

            let recursive {{size}} (lists: List(List({{elementType}}))) (acc: Int) =
                match lists with
                    | [] -> acc
                    | _first :: rest -> {{size}}(rest)(acc + 1)

            let {{run}} (nodes: List(Int)) (width: Int) =
                match {{state}}(references = 0, calls = 0, flow = {{empty}}(width)) |> {{count}}(7)(nodes) with
                    | {{state}} { references = references, calls = calls, flow = flow } ->
                        {{result}}

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{run}}({{nodes}})({{names.Between(1, 4)}}))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
