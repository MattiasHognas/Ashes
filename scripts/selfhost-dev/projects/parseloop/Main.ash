import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Token
// Parses the same source once per round. Nothing of a round outlives it, so whatever the
// reference-counted heap still holds at exit, beyond one round's worth, leaked in the lexer or the
// parser. The source must have no import or export header: `parseProgram` stops at one.
let recursive parseRounds (rounds: Int) (source: Str) (total: Int) =
    if rounds == 0
    then total
    else
        match parseProgram(source) with
            | ProgramParseResult { diagnostics = diagnostics } -> parseRounds(rounds - 1)(source)(total + 1 + Ashes.Collection.List.length(diagnostics))

let recursive messages (entries: List(DiagnosticEntry)) =
    match entries with
        | [] -> ""
        | DiagnosticEntry { message = message } :: rest -> message + " | " + messages(rest)

// The diagnostics of one parse first, so a source the parser stops early on is visible, then the
// round count.
let run (path: Str) (rounds: Int) =
    match Ashes.IO.File.readText(path) with
        | Error(message) -> "could not read " + path + ": " + message
        | Ok(source) ->
            (match parseProgram(source) with
                | ProgramParseResult { diagnostics = diagnostics } -> messages(diagnostics)) + " " + (0 |> parseRounds(rounds)(source) |> Ashes.Text.fromInt)

// The round count is the length of the second argument, so no number parsing is needed.
match Ashes.IO.args with
    | path :: unary :: [] ->
        unary
        |> Ashes.Text.byteLength
        |> run(path)
        |> Ashes.IO.print
    | _ -> Ashes.IO.print("usage: parseloop <source.ash> <x repeated once per round>")
