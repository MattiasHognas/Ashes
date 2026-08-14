import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
let assertDiagnosticBounds diagnostics sourceLength =
    (let recursive check remaining =
        match remaining with
            | [] -> Unit
            | diagnostic :: tail ->
                let span = diagnostic.span
                in
                    if span.start < 0
                    then test.fail("diagnostic starts before source")
                    else
                        if span.end < span.start
                        then test.fail("diagnostic ends before start")
                        else
                            if span.end > sourceLength
                            then test.fail("diagnostic ends after source")
                            else check(tail)
    in check(diagnostics))

let assertParserInvariants source =
    (let sourceLength = Ashes.Text.byteLength(source)
    in
        let result = parseExpression(source)
        in
            let spanChecked =
                match result.expression with
                    | ExprAt(span, _) ->
                        if span.start < 0
                        then test.fail("expression starts before source")
                        else
                            if span.end < span.start
                            then test.fail("expression ends before start")
                            else
                                if span.end <= sourceLength
                                then Unit
                                else test.fail("expression ends after source")
                    | _ -> test.fail("parsed expression is missing a span")
            in assertDiagnosticBounds(result.diagnostics)(sourceLength))

let recursive assertCorpus cases =
    match cases with
        | [] -> Unit
        | source :: tail ->
            let checked = assertParserInvariants(source)
            in assertCorpus(tail)

let run unit = assertCorpus(["", "0", "18446744073709551615u64", "- -0.5", "a + b * c << 2 == d", "head :: middle :: []", "value |> map(f) |?> finish |!> recover", "Module.Submodule.binding", "f()(1, 2)([3, 4])", "Point(x = 1, y = 2)", "map transform values", "(1, \"two\", '三')", "await task(Unit)", "perform Clock.now(Unit)", "if ready then run(Unit) else stop(Unit)", "given (left, right) -> left + right", "let recursive loop value = loop(value) in loop(0)", "let? value = result in value", "let! value = task in value", "match value with | Some(inner) when inner > 0 -> inner | None -> 0", "handle Clock.now(Unit) with | Clock.now(value) -> value | return(value) -> value", "point with x = 1, y = 2", "@", "(1, 2", "[1, 2", "1 2 )", "\"unterminated"])
