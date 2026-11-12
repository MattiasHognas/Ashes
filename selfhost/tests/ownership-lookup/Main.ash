import AshesCompiler.Frontend.Parser
import AshesCompiler.Semantics.OwnershipInference as Current
import Baseline as Old
import PrebuiltFixture as Prebuilt
let functions source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> Current.topLevelFunctions(program)
        | _ -> Ashes.IO.panic("parse failed")

let peek = "let peek h = Ashes.IO.File.readChunk(h)(2)\n"

let cases =
    [
        peek + "let wrap h = peek(h)\n",
        peek + "let mid h = peek(h)\nlet outer h = mid(h)\n",
        peek + peek + "let wrap h = peek(h)\n",
        peek + peek + peek + "let wrap h = peek(h)\n",
        peek + "let wrap h = (let peek = given (x) -> x in peek(h))\n",
        peek + "let wrap peek h = peek(h)\n",
        "let peekAt h n = Ashes.IO.File.readChunk(h)(n)\nlet part h = peekAt(h)\n",
        peek + "let over h = peek(h)(2)\n",
        "let recursive ping h = pong(h)\nand pong h = ping(h)\n",
        peek + "let keep h = (peek(h), h)\n",
        "let wrap h = unknown(h)\n"
    ]

let recursive check sources =
    match sources with
        | [] -> Ashes.IO.print("passed")
        | source :: rest ->
            let funcs = functions(source)
            in
                if Current.inferProgramParameterOwnership(funcs) == Old.inferProgramParameterOwnership(funcs)
                then check(rest)
                else Ashes.IO.panic("differential mismatch")

let parsedFixture = functions(peek + "let mid h = peek(h)\nlet outer h = mid(h)\n")

let fixture =
    if Prebuilt.matchesFixture(parsedFixture) && Prebuilt.matchesWrappedFixture(parsedFixture)
    then parsedFixture
    else Ashes.IO.panic("prebuilt AST differs from parsed fixture")

let expected = Old.inferProgramParameterOwnership(fixture)

let recursive plateau count old =
    if count == 0
    then Ashes.IO.print("done")
    else
        let actual =
            if old
            then Old.inferProgramParameterOwnership(fixture)
            else Current.inferProgramParameterOwnership(fixture)
        in
            if actual == expected
            then plateau(count - 1)(old)
            else Ashes.IO.panic("plateau mismatch")

match Ashes.IO.args with
    | "new100" :: [] -> plateau(100)(false)
    | "new1000" :: [] -> plateau(1000)(false)
    | "old100" :: [] -> plateau(100)(true)
    | "old1000" :: [] -> plateau(1000)(true)
    | _ -> check(cases)
