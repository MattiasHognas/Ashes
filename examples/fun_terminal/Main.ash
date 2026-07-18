import Ansi as ansi
import Game as game
import Input as input
import Physics as physics
import Physics.State
import Ashes.Console as console
import Ashes.IO as io
let frameMs = 33

let recursive collectInput pending deadline =
    (let now = console.monotonicMillis(Unit)
    in
        if now >= deadline
        then Some(pending)
        else
            match console.pollInput(deadline - now) with
                | None -> None
                | Some(chunk) -> collectInput(pending + chunk)(deadline))

let recursive hasQuit events =
    match events with
        | [] -> false
        | Quit :: _rest -> true
        | _event :: rest -> hasQuit(rest)

let render (state: State) =
    (let _drawn =
        state
        |> game.renderFrame
        |> io.write
    in state)

let recursive loop state pending =
    match collectInput(pending)(console.monotonicMillis(Unit) + frameMs) with
        | None -> state
        | Some(collected) ->
            match input.decode(collected) with
                | (events, leftover) ->
                    if hasQuit(events)
                    then state
                    else
                        let next =
                            events
                            |> physics.step(state)
                            |> render
                        in
                            if physics.finished(next)
                            then next
                            else loop(next)(leftover)

let setup _ = io.write(ansi.altScreenOn + ansi.clearScreen(Unit) + ansi.hideCursor + ansi.mouseOn + game.renderFrame(physics.initialState))

let runGame _ = loop(physics.initialState)("")

let cleanup (outcome: State) =
    (let _screen = io.write(ansi.mouseOff + ansi.showCursor + ansi.altScreenOff)
    in
        let _mode = console.restoreInput(Unit)
        in outcome)

let report (outcome: State) =
    outcome
    |> game.finalLine
    |> io.writeLine

let play _ =
    if console.enableRawInput(Unit)
    then
        Unit
        |> setup
        |> runGame
        |> cleanup
        |> report
    else io.writeLine("terminal-pong needs an interactive terminal; run it directly from a TTY, not through a pipe")

play(Unit)
