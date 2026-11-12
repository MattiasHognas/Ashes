using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>A visited map owns paths after the pending work list releases its references.</summary>
internal sealed class VisitedGenericMapOwnerTemplate() : LeakCombinationTemplate(
    "leak.visited-generic-map-owner", "visitedMapOwner", GeneratedFeature.Adt, GeneratedFeature.Record, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override IReadOnlyList<string> RequiredImports => ["Ashes.Collection.Map"];

    protected override string Declarations(LeakNames names)
        => $$"""
            type {{names.Of("Shape")}} =
                | {{names.Of("Named")}}(Int, Str, List({{names.Of("Shape")}}))
                | {{names.Of("Leaf")}}(Int)
            type {{names.Of("Path")}} =
                | byId: MapTree(Int, MapTree(Str, Int))
                | names: List({{names.Of("Shape")}})
            type {{names.Of("Work")}} =
                | {{names.Of("Visit")}}({{names.Of("Shape")}}, {{names.Of("Path")}})

            let recursive {{names.Of("count")}} paths =
                match paths with
                    | [] -> 0
                    | path :: rest -> Ashes.Collection.List.length(path.names) + {{names.Of("count")}}(rest)

            let recursive {{names.Of("consume")}} (work: List({{names.Of("Work")}})) (visited: MapTree(Int, List({{names.Of("Path")}}))) =
                match work with
                    | [] ->
                        match Ashes.Collection.Map.getInt(0)(visited) with
                            | None -> 0
                            | Some(paths) -> {{names.Of("count")}}(paths)
                    | {{names.Of("Visit")}}(shape, path) :: rest ->
                        let bucket = match Ashes.Collection.Map.getInt(0)(visited) with
                            | None -> []
                            | Some(paths) -> paths
                        in
                            match shape with
                                | {{names.Of("Named")}}(_, _, _) -> {{names.Of("consume")}}({{names.Of("Visit")}}({{names.Of("Leaf")}}(1))(path) :: rest)(Ashes.Collection.Map.setInt(0)(path :: bucket)(visited))
                                | {{names.Of("Leaf")}}(_) -> {{names.Of("consume")}}(rest)(visited)

            let {{names.Entry}} (seed: Int) =
                let shape = {{names.Of("Named")}}(seed)("A")([])
                in
                    let byName = Ashes.Collection.Map.setStr("A")(seed)(Ashes.Collection.Map.empty)
                    in
                        let path = {{names.Of("Path")}}(byId = Ashes.Collection.Map.setInt(seed)(byName)(Ashes.Collection.Map.empty), names = [shape])
                        in {{names.Of("consume")}}([{{names.Of("Visit")}}(shape)(path), {{names.Of("Visit")}}(shape)(path)])(Ashes.Collection.Map.empty)
            """;
}
