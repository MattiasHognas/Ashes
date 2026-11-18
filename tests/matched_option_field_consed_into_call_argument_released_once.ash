// expect: 1-2 10-11 1-2
// A field matched out of a function's Option result and consed onto a list passed to a call is
// retained for the cell once: the retain is released after the call and must not also be kept for
// a second release when the function returns. Released twice, the span list is freed while the
// state still holds it.
type Span =
    | start: Int
    | end: Int

type Origin =
    | name: Str
    | parent: Maybe(Str)
    deriving {Eq}

type Group =
    | sources: List((Maybe(Origin), Int, Span))
    | freed: List(Span)

type St =
    | label: Str
    | group: Group

let stateSources (state: St) =
    (let group = state.group
    in group.sources)

let stateFreed (state: St) =
    (let group = state.group
    in group.freed)

let withStateFreed value (state: St) =
    (let group = state.group
    in state with group = (group with freed = value))

let recursive slotSourceSpan (origin: Maybe(Origin)) (slot: Int) (sources: List((Maybe(Origin), Int, Span))) =
    match sources with
        | [] -> None
        | (owner, candidate, span) :: rest ->
            if candidate == slot && owner == origin
            then Some(span)
            else slotSourceSpan(origin)(slot)(rest)

let noteSpineFreedSlot (slot: Int) (state: St) =
    match state
    |> stateSources
    |> slotSourceSpan(Some(Origin(name = "f", parent = None)))(slot) with
        | Some(span) -> withStateFreed(span :: stateFreed(state))(state)
        | None -> state

let describe (s: Span) = Ashes.Text.fromInt(s.start) + "-" + Ashes.Text.fromInt(s.end)

let recursive noteAll (slots: List(Int)) (state: St) =
    match slots with
        | [] -> state
        | slot :: rest ->
            state
            |> noteSpineFreedSlot(slot)
            |> noteAll(rest)

let origin = Some(Origin(name = "f", parent = None))

let start = St(label = "s", group = Group(sources = [(origin, 7, Span(start = 1, end = 2)), (origin, 8, Span(start = 10, end = 11)), (None, 7, Span(start = 20, end = 21))], freed = []))

let finished = noteAll([7, 8, 7, 9])(start)

finished
|> stateFreed
|> Ashes.Collection.List.map(describe)
|> Ashes.Text.join(" ")
|> Ashes.IO.print
