// expect: makeList rc unknown
// A match arm returning a child extracted from a reference-counted list releases the parent there;
// that release is the parent's placed lifetime anchor, so it must stay correct on the arm where the
// list is empty. The node here carries no bytes provenances, so the lookup yields the empty list.
import Provenance
let node = buildProvenanceNode("makeList")(true)(false)(1)(None)([])(false)

let showBytes (bytes: BytesOwnershipProvenance) =
    match bytes with
        | BytesProvenanceUnknown -> "unknown"
        | BytesProvenanceFreshOwnedBuffer -> "fresh"
        | BytesProvenanceBorrowedView -> "borrowed"

match resolveResultProvenances([node]) with
    | (name, prov) :: _ ->
        match prov with
            | FunctionResultProvenance { rcEligible = rc, bytesProvenance = bytes } ->
                Ashes.IO.print(name + (if rc
                then " rc "
                else " no-rc ") + showBytes(bytes))
    | [] -> Ashes.IO.print("empty")
