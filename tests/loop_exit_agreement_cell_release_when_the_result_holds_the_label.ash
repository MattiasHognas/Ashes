// expect: 80
// The exit arm returns the agreed label itself; the arena cell's field is released only after
// the result holds its own reference.
type Definition =
    | MakeClosure(Int, Str)
    | Other(Int)
    | Nested(Definition)

type CaptureSite =
    | creator: List((Int, Definition))
    | sourceTemp: Int

type CaptureResolution =
    | CaptureKnown(Str)
    | CapturePending
    | CaptureUnknown

let recursive lookupAssociation key entries =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupAssociation(key)(tail)

let resolveLabel (definitions: List((Int, Definition))) (temp: Int) known =
    match lookupAssociation(temp)(definitions) with
        | Some(MakeClosure(_, label)) -> CaptureKnown(label)
        | Some(Other(_)) -> CapturePending
        | Some(Nested(_)) -> CapturePending
        | None -> CaptureUnknown

let recursive resolveGroup (sites: List(CaptureSite)) known (agreed: Maybe(Str)) (seen: List(Str)) (pending: Bool) =
    match sites with
        | [] ->
            if pending
            then CapturePending
            else
                match agreed with
                    | Some(label) -> CaptureKnown(label)
                    | None -> CapturePending
        | site :: tail ->
            match resolveLabel(site.creator)(site.sourceTemp)(known) with
                | CaptureUnknown -> CaptureUnknown
                | CapturePending -> resolveGroup(tail)(known)(agreed)(seen)(true)
                | CaptureKnown(label) ->
                    match agreed with
                        | None -> resolveGroup(tail)(known)(Some(label))(label :: seen)(pending)
                        | Some(previous) ->
                            if previous == label
                            then resolveGroup(tail)(known)(agreed)(label :: seen)(pending)
                            else CaptureUnknown

let probe (n: Int) =
    (let definitions = [(1, MakeClosure(1)("lambda_" + Ashes.Text.fromInt(n % 10))), (2, Other(2))]
    in
        match resolveGroup([CaptureSite(creator = definitions, sourceTemp = 1)])([])(None)([])(false) with
            | CaptureKnown(l) -> Ashes.Text.length(l)
            | CapturePending -> 7
            | CaptureUnknown -> 9)

let recursive loop (n: Int) (acc: Int) =
    if n == 0
    then acc
    else loop(n - 1)(acc + probe(n))
in
    0
    |> loop(10)
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
