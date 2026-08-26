// Times the self-hosted compiler phases over a corpus of .ash files.
//
// Usage: stage1-bench <file-list> <iterations> <phase>
//   <file-list>  a text file with one .ash path per line
//   <iterations> how many times each phase runs; the fastest run is reported
//   <phase>      header | lex | parse | format | infer | all
//
// Each phase prints one line `phase<TAB>count<TAB>milliseconds`, where count is the phase's own
// checksum (files whose header parsed, tokens produced, files parsed without diagnostics, bytes
// formatted, import-free programs inferred without error) so the work cannot be elided.

import Ashes.IO as io
import Ashes.IO.File as file
import Ashes.IO.Console as console
import Ashes.Text as text
import Ashes.Collection.List.length as listLength
import Ashes.Collection.List.reverse as reverseList
import Ashes.Internal.deepCopy as deepCopy
import AshesCompiler.Frontend.ImportHeader
import AshesCompiler.Frontend.Lexer
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Formatter.Formatter
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.StandardTraits
type BenchSource =
    | source: Str
    | body: Str
    | importFree: Bool

let recursive readSources paths acc =
    match paths with
        | [] -> reverseList(acc)
        | path :: rest ->
            if text.trim(path) == ""
            then readSources(rest)(acc)
            else
                match file.readText(text.trim(path)) with
                    | Error(_) -> readSources(rest)(acc)
                    | Ok(source) ->
                        match parseImportHeader(source) with
                            | Error(_) -> readSources(rest)(acc)
                            | Ok(ParsedImportHeader { imports = imports, sourceWithoutImports = body }) ->
                                readSources(rest)(
                                    BenchSource(
                                        source = deepCopy(source),
                                        body = deepCopy(body),
                                        importFree = listLength(imports) == 0
                                    ) :: acc
                                )

let recursive countHeaders sources acc =
    match sources with
        | [] -> acc
        | BenchSource { source = source } :: rest ->
            match parseImportHeader(source) with
                | Ok(_) -> countHeaders(rest)(acc + 1)
                | Error(_) -> countHeaders(rest)(acc)

let recursive countTokens sources acc =
    match sources with
        | [] -> acc
        | BenchSource { body = body } :: rest ->
            match tokenize(body) with
                | LexerResult { tokens = tokens } -> countTokens(rest)(acc + listLength(tokens))

let recursive countParsed sources acc =
    match sources with
        | [] -> acc
        | BenchSource { body = body } :: rest ->
            match parseProgram(body) with
                | ProgramParseResult { diagnostics = [] } -> countParsed(rest)(acc + 1)
                | _ -> countParsed(rest)(acc)

let recursive parseAll sources acc =
    match sources with
        | [] -> reverseList(acc)
        | BenchSource { body = body, importFree = importFree } :: rest ->
            match parseProgram(body) with
                | ProgramParseResult { program = program, diagnostics = [] } -> parseAll(rest)((program, importFree) :: acc)
                | _ -> parseAll(rest)(acc)

let recursive countFormatted programs acc =
    match programs with
        | [] -> acc
        | (program, _importFree) :: rest -> countFormatted(rest)(acc + text.byteLength(formatProgram(program)))

let recursive countInferred environment programs acc =
    match programs with
        | [] -> acc
        | (_program, false) :: rest -> countInferred(environment)(rest)(acc)
        | (program, true) :: rest ->
            match inferProgramFromPackage("bench")(environment)(program) with
                | ProgramInferenceResult { error = None } -> countInferred(environment)(rest)(acc + 1)
                | _ -> countInferred(environment)(rest)(acc)

let recursive fastestRun work iterations bestMillis lastCount =
    if iterations <= 0
    then (bestMillis, lastCount)
    else
        let start = console.monotonicMillis(Unit)
        in
            let count = work(Unit)
            in
                let elapsed = console.monotonicMillis(Unit) - start
                in
                    if elapsed < bestMillis
                    then fastestRun(work)(iterations - 1)(elapsed)(count)
                    else fastestRun(work)(iterations - 1)(bestMillis)(count)

let report name work iterations =
    match fastestRun(work)(iterations)(1000000000)(0) with
        | (bestMillis, count) -> io.print(name + "\t" + text.fromInt(count) + "\t" + text.fromInt(bestMillis))

let runPhase phase sources programs iterations =
    match phase with
        | "header" ->
            report("header")(given (_) -> countHeaders(sources)(0))(iterations)
        | "lex" ->
            report("lex")(given (_) -> countTokens(sources)(0))(iterations)
        | "parse" ->
            report("parse")(given (_) -> countParsed(sources)(0))(iterations)
        | "format" ->
            report("format")(given (_) -> countFormatted(programs)(0))(iterations)
        | "infer" ->
            let environment = standardTraitEnvironment(Unit)
            in
                report("infer")(given (_) -> countInferred(environment)(programs)(0))(iterations)
        | _ -> io.print("unknown phase " + phase)

let runPhases phase sources programs iterations =
    if phase == "all"
    then
        let _ = runPhase("header")(sources)(programs)(iterations)
        in
            let _ = runPhase("lex")(sources)(programs)(iterations)
            in
                let _ = runPhase("parse")(sources)(programs)(iterations)
                in
                    let _ = runPhase("format")(sources)(programs)(iterations)
                    in runPhase("infer")(sources)(programs)(iterations)
    else runPhase(phase)(sources)(programs)(iterations)

match io.args with
    | listPath :: iterationsText :: phase :: [] ->
        match file.readText(listPath) with
            | Error(error) -> io.print("cannot read file list: " + error)
            | Ok(listText) ->
                match text.parseInt(text.trim(iterationsText)) with
                    | Error(_) -> io.print("iterations must be an integer")
                    | Ok(iterations) ->
                        let sources = readSources(text.split(listText)("\n"))([])
                        in
                            let programs = parseAll(sources)([])
                            in runPhases(phase)(sources)(programs)(iterations)
    | _ -> io.print("usage: stage1-bench <file-list> <iterations> <header|lex|parse|format|infer|all>")
