// A freshly built reference-counted list handed to a callee that stores it into the record it
// returns must stay alive: the caller holds the only reference, a list parameter can never be
// entry-normalized (so the callee's adoption bit is always clear and the caller's conditional retain
// never fires), and a stored parameter is retained by nobody. The caller therefore must not release
// it after the call — the argument's ownership moves to the callee, which the result reach proves by
// reporting the parameter kept whole. The store goes through a lambda applied to the state, the shape
// a `|>` stage desugars to, whose body reads the list as a capture rather than a parameter; reaching
// through it is what keeps the fact alive. Each round allocates again so a freed cell would be reused
// before it is read back.
// expect: 4086000
import Ashes.IO as io
import Ashes.Text as text
type Candidate =
    | slot: Int
    | strManaged: Bool

type Frame =
    | label: Int
    | managedStrs: List((Int, Int))

type State =
    | frame: Maybe(Frame)
    | retired: List(Int)

let recursive managedStrSlotsOf (candidates: List(Candidate)) (active: Int) =
    match candidates with
        | [] -> []
        | Candidate { slot = slot, strManaged = true } :: rest -> (slot, slot + active) :: managedStrSlotsOf(rest)(active)
        | _candidate :: rest -> managedStrSlotsOf(rest)(active)

let recursive lookupSlot (slot: Int) (entries: List((Int, Int))) =
    match entries with
        | [] -> None
        | (candidate, found) :: rest ->
            if candidate == slot
            then Some(found)
            else lookupSlot(slot)(rest)

let finishPlacement (frame: Frame) (managedStrs: List((Int, Int))) (state: State) =
    state |> (given (current: State) -> current with frame = Some((frame with managedStrs = managedStrs)))

let recursive candidatesOf (count: Int) (acc: List(Candidate)) =
    if count == 0
    then acc
    else candidatesOf(count - 1)(Candidate(slot = count, strManaged = count % 2 == 0) :: acc)

let finalize (frame: Frame) (state: State) =
    []
    |> candidatesOf(40)
    |> (given (candidates: List(Candidate)) ->
        frame.label
        |> managedStrSlotsOf(candidates)
        |> (given (managedStrs: List((Int, Int))) -> state |> finishPlacement(frame)(managedStrs)))

let lookupAll (state: State) =
    match state.frame with
        | Some(frame) ->
            match (lookupSlot(2)(frame.managedStrs), lookupSlot(40)(frame.managedStrs)) with
                | (Some(first), Some(last)) -> first + last
                | _ -> -1000000
        | None -> -1000000

let recursive churn (count: Int) (acc: List((Int, Int))) =
    if count == 0
    then acc
    else churn(count - 1)((count, count) :: acc)

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        let state = finalize(Frame(label = remaining, managedStrs = []))(State(frame = None, retired = []))
        in
            let noise = churn(64)([])
            in
                match noise with
                    | _ -> loop(remaining - 1)(total + lookupAll(state))

0
|> loop(2000)
|> text.fromInt
|> io.print
