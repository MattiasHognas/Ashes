type Origin =
    | label: Str

type IrProgram =
    | name: Str
    | items: List(Int)

type LoweringResult =
    | program: Maybe(IrProgram)
    | error: Maybe(Str)
    | valuePlacements: List((Int, Maybe(Origin), Bool))
    | joinRepresentations: List((Maybe(Origin), Int, Bool, Bool))

type Stitched =
    | source: Str
    | program: List(Int)

let lowerProgram (k: Int) (program: List(Int)) =
    if k < 0
    then LoweringResult(program = None, error = Some("bad"), valuePlacements = [], joinRepresentations = [])
    else LoweringResult(program = Some(IrProgram(name = "p" + Ashes.Text.fromInt(k), items = k :: program)), error = None, valuePlacements = [(k, Some(Origin(label = "o" + Ashes.Text.fromInt(k))), true)], joinRepresentations = [(None, k, false, true)])

let optimize (p: IrProgram) = IrProgram(name = p.name + "o", items = 0 :: p.items)

let errorText (error: Str) = "error: " + error

let lowerStitchedProgram (k: Int) program =
    match lowerProgram(k)(program) with
        | LoweringResult { program = Some(lowered), error = None, valuePlacements = valuePlacements, joinRepresentations = joinRepresentations } -> Ok((lowered, optimize(lowered), (valuePlacements, joinRepresentations)))
        | LoweringResult { error = Some(error) } ->
            error
            |> errorText
            |> Error
        | _ -> Error("none")

let lowerStitched (k: Int) stitched =
    match stitched with
        | Stitched { program = program } ->
            match lowerStitchedProgram(k)(program) with
                | Error(message) -> Error(message)
                | Ok((lowered, optimized, valuePlacements)) -> Ok((stitched, lowered, optimized, valuePlacements))

let report (explain: Bool) (s: Stitched) (lowered: IrProgram) placements (optimized: IrProgram) =
    if explain
    then Ashes.Collection.List.length(lowered.items) + Ashes.Collection.List.length(optimized.items)
    else 0

let emitAndLink (optimized: IrProgram) =
    optimized.items
    |> Ashes.Collection.List.length
    |> Ok

let compileStitched (k: Int) stitched =
    match lowerStitched(k)(stitched) with
        | Error(message) -> Error(message)
        | Ok((stitchedProject, lowered, optimized, valuePlacements)) ->
            optimized
            |> report(k > 3)(stitchedProject)(lowered)(valuePlacements)
            |> (given (_) -> emitAndLink(optimized))

let recursive loop (remaining: Int) (total: Int) =
    if remaining == 0
    then total
    else
        match compileStitched(remaining)(Stitched(source = "s" + Ashes.Text.fromInt(remaining), program = [remaining, 2])) with
            | Ok(n) -> loop(remaining - 1)(total + n)
            | Error(_) -> loop(remaining - 1)(total)

0
|> loop(ROUNDS)
|> Ashes.IO.print
