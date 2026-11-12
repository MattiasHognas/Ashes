// expect: 1000
import Ashes.Collection.Map
type BytesOwnershipProvenance =
    | BytesProvenanceUnknown
    | BytesProvenanceFreshOwnedBuffer

type FunctionResultProvenance =
    | rcEligible: Bool
    | forwardsTo: Maybe(Str)
    | bytesProvenance: BytesOwnershipProvenance

let recursive bytesProvenancesOf (byName: MapTree(Str, BytesOwnershipProvenance)) (provenances: List((Str, FunctionResultProvenance))) =
    match provenances with
        | [] -> byName
        | (name, FunctionResultProvenance { bytesProvenance = provenance }) :: rest ->
            bytesProvenancesOf(Ashes.Collection.Map.setStr(name)(provenance)(byName))(rest)

let recursive build remaining acc =
    if remaining == 0
    then acc
    else
        let name = Ashes.Text.fromInt(remaining)
        in
            let provenance =
                if remaining == 1
                then BytesProvenanceFreshOwnedBuffer
                else BytesProvenanceUnknown
            in build(remaining - 1)((name, FunctionResultProvenance(rcEligible = false, forwardsTo = None, bytesProvenance = provenance)) :: acc)

let recursive verify remaining byName =
    if remaining == 0
    then 0
    else
        match Ashes.Collection.Map.getStr(Ashes.Text.fromInt(remaining))(byName) with
            | None -> Ashes.IO.panic("missing")
            | Some(provenance) ->
                let valid =
                    match provenance with
                        | BytesProvenanceUnknown -> true
                        | BytesProvenanceFreshOwnedBuffer -> true
                        | _ -> false
                in
                    if valid
                    then 1 + verify(remaining - 1)(byName)
                    else Ashes.IO.panic("wrong provenance")

let runDirect count =
    (let values = build(count)([])
    in
        let result = bytesProvenancesOf(Ashes.Collection.Map.empty)(values)
        in verify(count)(result))

let freshProvenance remaining =
    match build(remaining % 2 + 1)([]) with
        | (_, FunctionResultProvenance { bytesProvenance = provenance }) :: _ -> provenance
        | [] -> BytesProvenanceUnknown

let recursive freshArguments remaining (byName: MapTree(Str, BytesOwnershipProvenance)) =
    if remaining == 0
    then byName
    else
        byName
        |> Ashes.Collection.Map.setStr(Ashes.Text.fromInt(remaining))(freshProvenance(remaining))
        |> freshArguments(remaining - 1)

let freshCount =
    Ashes.Collection.Map.empty
    |> freshArguments(1000)
    |> verify(1000)

if freshCount == 1000
then
    1000
    |> runDirect
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
else Ashes.IO.panic("fresh argument")
