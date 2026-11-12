using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>A copied generic argument must not inherit the original pattern field's RC ownership.</summary>
internal sealed class CopiedGenericEnumArgumentTemplate() : LeakCombinationTemplate(
    "leak.copied-generic-enum-argument", "copiedGenericEnum", GeneratedFeature.Adt, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override IReadOnlyList<string> RequiredImports => ["Ashes.Collection.Map"];

    protected override string Declarations(LeakNames names)
        => $$"""
            type {{names.Of("BytesOwnershipProvenance")}} =
                | {{names.Of("BytesProvenanceUnknown")}}
                | {{names.Of("BytesProvenanceFreshOwnedBuffer")}}
            type {{names.Of("FunctionResultProvenance")}} =
                | rcEligible: Bool
                | forwardsTo: Maybe(Str)
                | bytesProvenance: {{names.Of("BytesOwnershipProvenance")}}
            let recursive {{names.Of("bytesProvenancesOf")}} (byName: MapTree(Str, {{names.Of("BytesOwnershipProvenance")}})) (provenances: List((Str, {{names.Of("FunctionResultProvenance")}}))) =
                match provenances with
                    | [] -> byName
                    | (name, {{names.Of("FunctionResultProvenance")}} { bytesProvenance = provenance }) :: rest ->
                        {{names.Of("bytesProvenancesOf")}}(Ashes.Collection.Map.setStr(name)(provenance)(byName))(rest)

            let recursive {{names.Of("build")}} remaining acc =
                if remaining == 0
                then acc
                else
                    let name = Ashes.Text.fromInt(remaining)
                    in
                        let provenance =
                            if remaining == 1
                            then {{names.Of("BytesProvenanceFreshOwnedBuffer")}}
                            else {{names.Of("BytesProvenanceUnknown")}}
                        in {{names.Of("build")}}(remaining - 1)((name, {{names.Of("FunctionResultProvenance")}}(rcEligible = false, forwardsTo = None, bytesProvenance = provenance)) :: acc)

            let recursive {{names.Of("verify")}} remaining byName =
                if remaining == 0
                then 0
                else
                    match Ashes.Collection.Map.getStr(Ashes.Text.fromInt(remaining))(byName) with
                        | None -> Ashes.IO.panic("missing")
                        | Some(provenance) ->
                            let valid =
                                match provenance with
                                    | {{names.Of("BytesProvenanceUnknown")}} -> true
                                    | {{names.Of("BytesProvenanceFreshOwnedBuffer")}} -> true
                                    | _ -> false
                            in
                                if valid
                                then 1 + {{names.Of("verify")}}(remaining - 1)(byName)
                                else Ashes.IO.panic("wrong provenance")

            let {{names.Of("freshProvenance")}} remaining =
                match {{names.Of("build")}}(remaining % 2 + 1)([]) with
                    | (_, {{names.Of("FunctionResultProvenance")}} { bytesProvenance = provenance }) :: _ -> provenance
                    | [] -> {{names.Of("BytesProvenanceUnknown")}}

            let recursive {{names.Of("freshArguments")}} remaining (byName: MapTree(Str, {{names.Of("BytesOwnershipProvenance")}})) =
                if remaining == 0
                then byName
                else
                    byName
                    |> Ashes.Collection.Map.setStr(Ashes.Text.fromInt(remaining))({{names.Of("freshProvenance")}}(remaining))
                    |> {{names.Of("freshArguments")}}(remaining - 1)


            let {{names.Entry}} (seed: Int) =
                let count = 3 + seed % 2
                in
                    (let values = {{names.Of("build")}}(count)([])
                    in
                        let result = {{names.Of("bytesProvenancesOf")}}(Ashes.Collection.Map.empty)(values)
                        in {{names.Of("verify")}}(count)(result) + {{names.Of("verify")}}(count)({{names.Of("freshArguments")}}(count)(Ashes.Collection.Map.empty)))

            """;
}
