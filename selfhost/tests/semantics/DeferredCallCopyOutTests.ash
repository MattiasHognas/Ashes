// A call whose result type is still unresolved when the call closes its arena window routes the
// result through a local slot and reads it back, and the copy-out block that belongs between the
// store and the reload is resolved once the body is lowered. Resolving it walks the finished body
// looking for that reload; the entry normalizations and direct-reuse entry copies a loop frame
// splices in are emitted after the copy-out was recorded, so a reload identified by its position
// in the body names the wrong instruction by the time the walk runs, and the walk then removes an
// unrelated instruction. These checks lower a producer whose reload is preceded by such a splice
// and assert that every temp the body reads is defined before it is read.
import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
import LoweredIrFixtures.functionLines
export (
    value runDeferredCallCopyOutTests,
)

// A non-tail cons producer over a self-referential ADT: the element type's layout is unresolved
// at the self call, so its result copy-out is deferred, and the loop frame splices its entry
// normalization in ahead of the body afterwards.
let recursiveAdtProducerSource = "type Pattern =\n    | PatternVar(Str)\n    | PatternConstructor(Str, List(Pattern))\n\nlet recursive variablePatterns names =\n    match names with\n        | [] -> []\n        | name :: tail -> PatternVar(name) :: variablePatterns(tail)\n\nlet recursive countPatterns patterns =\n    match patterns with\n        | [] -> 0\n        | _head :: tail -> 1 + countPatterns(tail)\n\nAshes.IO.print(Ashes.Text.fromInt(countPatterns(variablePatterns([\"a\", \"b\", \"c\"]))))\n"

let parsedProgram (source: Str) =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let loweredLines (source: Str) =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { program = Some(program), error = None } -> formatIr(program)(LoweredIr)(None)
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("program lowering failed: " + text))
        | _ -> test.fail("program lowering produced no program")

// The integer after `key=` in one whitespace-separated operand token, when the token carries it.
let operandOf (key: Str) (token: Str) =
    match Ashes.Text.split(token)("=") with
        | name :: value :: [] ->
            if name == key
            then
                match Ashes.Text.parseInt(value) with
                    | Ok(number) -> Some(number)
                    | Error(_message) -> None
            else None
        | _ -> None

let recursive firstOperand (key: Str) (tokens: List(Str)) =
    match tokens with
        | [] -> None
        | token :: rest ->
            match operandOf(key)(token) with
                | Some(value) -> Some(value)
                | None -> firstOperand(key)(rest)

let lineOperand (key: Str) (line: Str) =
    " "
    |> Ashes.Text.split(line)
    |> firstOperand(key)

// Every temp an instruction defines: the IR text spells a definition as this line's `Target`.
let definedBy (line: Str) = lineOperand("Target")(line)

let recursive definesTemp (temp: Int) (lines: List(Str)) =
    match lines with
        | [] -> false
        | line :: rest -> definedBy(line) == Some(temp) || definesTemp(temp)(rest)

// The lines up to, but not including, the first one carrying `needle`.
let recursive linesBefore (needle: Str) (lines: List(Str)) (collected: List(Str)) =
    match lines with
        | [] -> test.fail("no line containing " + needle)
        | line :: rest ->
            if Ashes.Text.contains(line)(needle)
            then (Ashes.Collection.List.reverse(collected), line)
            else linesBefore(needle)(rest)(line :: collected)

// The argument a call receives must be defined before the call reads it. The defect this guards
// against removed the reload that defined it, leaving the call reading a temp nothing bound.
let expectCallArgumentDefinedBeforeTheCall unit =
    match recursiveAdtProducerSource
    |> loweredLines
    |> functionLines("variablePatterns")
    |> (given (lines: List(Str)) -> linesBefore("CallClosure")(lines)([])) with
        | (before, callLine) ->
            match lineOperand("ArgTemp")(callLine) with
                | None -> test.fail("the producer's self call carries no ArgTemp: " + callLine)
                | Some(argumentTemp) ->
                    if definesTemp(argumentTemp)(before)
                    then Unit
                    else
                        test.fail(
                            "the self call reads temp " + Ashes.Text.fromInt(argumentTemp) + " before anything defines it:\n" + Ashes.Text.join("\n")(before) + "\n" + callLine
                        )

let runDeferredCallCopyOutTests unit =
    Unit
    |> expectCallArgumentDefinedBeforeTheCall
    |> (given (_) -> Ashes.IO.print("deferred call copy-out tests passed"))
