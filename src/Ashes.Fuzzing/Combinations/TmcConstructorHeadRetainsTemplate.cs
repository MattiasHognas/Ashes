using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>An arena constructor head releases its field retains after RC normalization.</summary>
internal sealed class TmcConstructorHeadRetainsTemplate() : LeakCombinationTemplate(
    "leak.tmc-constructor-head-retains", "tmcConstructorHead", GeneratedFeature.Adt, GeneratedFeature.Record, GeneratedFeature.TailCall, GeneratedFeature.Match)
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

            let recursive {{names.Of("prepend")}} types path pending =
                match types with
                    | [] -> pending
                    | head :: rest -> {{names.Of("Visit")}}(head)(path) :: {{names.Of("prepend")}}(rest)(path)(pending)

            let recursive {{names.Of("consume")}} work =
                match work with
                    | [] -> 0
                    | {{names.Of("Visit")}}(_, path) :: rest -> Ashes.Collection.List.length(path.names) + {{names.Of("consume")}}(rest)

            let {{names.Entry}} (seed: Int) =
                let shape = {{names.Of("Named")}}(seed)("A")([])
                in
                    let byName = Ashes.Collection.Map.setStr("A")(seed)(Ashes.Collection.Map.empty)
                    in
                        let path = {{names.Of("Path")}}(byId = Ashes.Collection.Map.setInt(seed)(byName)(Ashes.Collection.Map.empty), names = [shape])
                        in {{names.Of("consume")}}({{names.Of("prepend")}}([shape, shape])(path)([])) + Ashes.Collection.List.length(path.names)
            """;
}
