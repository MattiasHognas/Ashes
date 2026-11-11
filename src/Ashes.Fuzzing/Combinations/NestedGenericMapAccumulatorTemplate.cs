using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>A changing nested generic map owns the RC children of each successor.</summary>
internal sealed class NestedGenericMapAccumulatorTemplate() : LeakCombinationTemplate(
    "leak.nested-generic-map-accumulator", "nestedGenericMap", GeneratedFeature.Adt, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override IReadOnlyList<string> RequiredImports => ["Ashes.Collection.Map"];

    protected override string Declarations(LeakNames names)
    {
        string payload = names.Of("MapPayload");
        string named = names.Of("MapNamed");
        string leaf = names.Of("MapLeaf");
        string bucket = names.Of("mapBucket");
        string build = names.Of("buildMap");
        string key = names.Word();
        return $$"""
            type {{payload}} =
                | {{named}}(Int, Str, List({{payload}}))
                | {{leaf}}(Int)

            let {{bucket}} (id: Int) (name: Str) (index: MapTree(Int, MapTree(Str, List({{payload}})))) =
                match Ashes.Collection.Map.getInt(id)(index) with
                    | None -> []
                    | Some(byName) ->
                        match Ashes.Collection.Map.getStr(name)(byName) with
                            | None -> []
                            | Some(values) -> values

            let recursive {{build}} (roots: List({{payload}})) (index: MapTree(Int, MapTree(Str, List({{payload}})))) =
                match roots with
                    | [] -> index
                    | root :: rest ->
                        match root with
                            | {{named}}(id, name, _children) ->
                                let byName =
                                    match Ashes.Collection.Map.getInt(id)(index) with
                                        | None -> Ashes.Collection.Map.empty
                                        | Some(existing) -> existing
                                in
                                    let values = {{bucket}}(id)(name)(index)
                                    in
                                        index
                                        |> Ashes.Collection.Map.setInt(id)(Ashes.Collection.Map.setStr(name)(root :: values)(byName))
                                        |> {{build}}(rest)
                            | _ -> {{build}}(rest)(index)

            let {{names.Entry}} (seed: Int) =
                let roots = [{{named}}(40)({{key}})([{{leaf}}(seed)]), {{named}}(41)({{key}})([{{leaf}}(seed + 1)]), {{named}}(41)({{key}})([{{leaf}}(seed + 2)])]
                in
                    {{build}}(roots)(Ashes.Collection.Map.empty)
                    |> {{bucket}}(41)({{key}})
                    |> Ashes.Collection.List.length
                    |> (given (count: Int) -> count + seed)
            """;
    }
}
