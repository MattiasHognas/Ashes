import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserProgramTests
let run unit =
    (let source = "let recursive even value = if value == 0 then true else odd(value - 1)\nand odd value = if value == 0 then false else even(value - 1)\neven(10)"
    in
        match parseProgram(source) with
            | ProgramParseResult { program = ProgramSyntax { items = item :: [], body = _body }, diagnostics = diagnostics } ->
                let diagnosticsChecked = test.assertEqual([])(diagnostics)
                in
                    match ParserProgramTests.unspanTopLevel(item) with
                        | TopLevelRecursiveGroup(LetBindingSyntax { name = firstName, value = _firstValue, sugarParameters = _firstParameters, typeAnnotation = _firstAnnotation, requirements = _firstRequirements } :: LetBindingSyntax { name = secondName, value = _secondValue, sugarParameters = _secondParameters, typeAnnotation = _secondAnnotation, requirements = _secondRequirements } :: []) -> test.assertEqual(("even", "odd"))((firstName, secondName))
                        | _ -> test.fail("expected recursive group")
            | _ -> test.fail("expected one recursive item"))
