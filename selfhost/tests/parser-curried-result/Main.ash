import AshesCompiler.Frontend.Parser
let peek = "let peek h = Ashes.IO.File.readChunk(h)(2)\n"

let source = peek + "let mid h = peek(h)\nlet outer h = mid(h)\n"

match parseProgram(source) with
    | ProgramParseResult { program = _program, diagnostics = [] } -> Ashes.IO.print("parsed")
    | ProgramParseResult { program = _program, diagnostics = _diagnostics } -> Ashes.IO.panic("parse failed")
