import Ashes.Test as test
import Ashes.Text.join
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Formatter.Formatter
import AshesCompiler.Formatter.SourceFormatting
let expressionFrom source =
    match parseExpression(source) with
        | ExpressionParseResult { expression = expression, diagnostics = [] } -> expression
        | _ -> test.fail("source should parse")

let typeFrom source =
    match parseTypeExpression(source) with
        | TypeExpressionParseResult { typeExpression = typeExpression, diagnostics = [] } -> typeExpression
        | _ -> test.fail("type source should parse")

let recursive firstMatchPattern expression =
    match expression with
        | ExprAt(_span, inner) -> firstMatchPattern(inner)
        | ExprMatch(_value, (pattern, _body, _guard) :: _tail, _position) -> pattern
        | _ -> test.fail("source should contain a match case")

let patternFrom source =
    source
    |> expressionFrom
    |> firstMatchPattern

let programFrom source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | _ -> test.fail("program source should parse")

let assertProgram expected source =
    (let actual =
        source
        |> programFrom
        |> formatProgram
    in
        if expected != actual
        then test.fail("expected program:\n" + expected + "actual program:\n" + actual)
        else
            actual
            |> programFrom
            |> formatProgram
            |> test.assertEqual(actual))

let assertExpression expected source =
    (let actual =
        source
        |> expressionFrom
        |> formatExpression
    in
        if expected == actual
        then Unit
        else test.fail("expected formatter output:\n" + expected + "actual formatter output:\n" + actual))

let assertIdempotent source =
    (let first =
        source
        |> expressionFrom
        |> formatExpression
    in
        first
        |> expressionFrom
        |> formatExpression
        |> test.assertEqual(first))

let assertExpressionWithOptions expected options source =
    (let actual =
        formatExpressionWithOptions(expressionFrom(source))(false)(options)
    in
        if expected == actual
        then Unit
        else test.fail("expected formatter output:\n" + expected + "actual formatter output:\n" + actual))

let assertExpressionWithPipelines expected source =
    (let actual =
        formatExpressionWithOptions(expressionFrom(source))(true)(formattingOptionsDefault)
    in
        if expected == actual
        then Unit
        else test.fail("expected formatter output:\n" + expected + "actual formatter output:\n" + actual))

let multilineRecordExpected =
    join("\n")([
        "ExternalFunctionAbi(",
        "    name = name,",
        "    parameters = parameters,",
        "    runtimeCapabilities = capabilities",
        ")",
        ""
    ])

let multilineRecordSource =
    join("\n")([
        "ExternalFunctionAbi(",
        "name=name,",
        "parameters=parameters,",
        "runtimeCapabilities=capabilities",
        ")"
    ])

let assertPattern expected pattern =
    (let actual = formatPattern(pattern)
    in
        if expected == actual
        then Unit
        else
            test.fail(
                "expected pattern: " + expected + "\nactual pattern: " + actual + "\nactual syntax: " + Ashes.Trait.Show.show(
                    pattern
                )
            ))

let assertReinserted expected original formatted lineEnding =
    (let actual = reinsertStandaloneCommentLines(original)(formatted)(lineEnding)
    in
        if expected == actual
        then Unit
        else test.fail("expected reinserted source:\n" + expected + "actual reinserted source:\n" + actual))

let assertFormattedSource expected source =
    match formatSource(source) with
        | Ok(actual) ->
            if expected != actual
            then test.fail("expected formatted source:\n" + expected + "actual formatted source:\n" + actual)
            else
                match formatSource(actual) with
                    | Ok(again) -> test.assertEqual(actual)(again)
                    | Error(error) -> test.fail("formatted source must format again: " + Ashes.Trait.Show.show(error))
        | Error(error) -> test.fail("source must format: " + Ashes.Trait.Show.show(error))

let expectSplitSourceLinesDropsTheTerminalNewlineAndCarriageReturns unit =
    unit
    |> (given (_) ->
        "a\r\n\r\nb\r\n"
        |> splitSourceLines
        |> test.assertEqual(["a", "", "b"]))
    |> (given (_) ->
        "a\n\n"
        |> splitSourceLines
        |> test.assertEqual(["a", ""]))
    |> (given (_) ->
        ""
        |> splitSourceLines
        |> test.assertEqual([]))

let expectCommentFollowsItsNextAnchor unit = assertReinserted("let a = 1\n\n// about b\nlet b = 2\n")("let a=1\n// about b\nlet b=2\n")("let a = 1\n\nlet b = 2\n")("\n")

let expectCommentFallsBackToThePreviousAnchor unit = assertReinserted("let a = 1\n// trailing note\n")("let a=1\n// trailing note\nlet b=2\n")("let a = 1\n")("\n")

let expectCommentWithoutAnchorsGoesToTheTop unit = assertReinserted("// only\nbar\n")("// only\nfoo\n")("bar\n")("\n")

let expectRepeatedLinesAnchorByOccurrence unit = assertReinserted("x\nx\n// second\nx\n// after last\n")("x\nx\n// second\nx\n// after last\n")("x\nx\nx\n")("\n")

let expectReinsertionJoinsWithTheRequestedLineEnding unit = assertReinserted("let a = 1\r\n\r\n// about b\r\nlet b = 2\r\n")("let a=1\r\n// about b\r\nlet b=2\r\n")("let a = 1\n\nlet b = 2\n")("\r\n")

let expectCommentTextIsPreservedVerbatim unit = assertReinserted("let f x =\n      // inner\n    x\n")("let f x =\n      // inner\n      x\n")("let f x =\n    x\n")("\n")

let expectLeadingCommentsAreSplitFromTheBody unit =
    match extractLeadingComments("// header\n\nlet a = 1\n// not leading\nlet b = 2\n") with
        | (leading, body) ->
            unit
            |> (given (_) -> test.assertEqual(["// header", ""])(leading))
            |> (given (_) -> test.assertEqual("let a = 1\n// not leading\nlet b = 2")(body))

let expectImportsAreRenderedCanonically unit =
    match extractImports("import   Ashes.Text\tas T\nimport Ashes.Collection.List.map\nlet a = 1\n  import Ashes.Test as test\nlet b = 2") with
        | Ok((imports, body)) ->
            unit
            |> (given (_) -> test.assertEqual(["import Ashes.Text as T", "import Ashes.Collection.List.map", "import Ashes.Test as test"])(imports))
            |> (given (_) -> test.assertEqual("let a = 1\nlet b = 2")(body))
        | Error(error) -> test.fail("imports must extract: " + Ashes.Trait.Show.show(error))

let expectMalformedImportLinesAreRejected unit =
    match extractImports("let a = 1\nimport 9bad\n") with
        | Error(InvalidImportLine(2, "import 9bad")) -> Unit
        | Error(error) -> test.fail("a malformed import must name its line, got " + Ashes.Trait.Show.show(error))
        | Ok(_) -> test.fail("a malformed import must be rejected")

let expectFormatSourceKeepsHeaderImportsAndComments unit =
    assertFormattedSource(
        "// header\n\nimport Ashes.Text as T\nimport Ashes.Collection.List.map\nlet a = 1\n\n// about b\nlet b = 2\n",
        "// header\n\nimport Ashes.Text as T\nimport   Ashes.Collection.List.map\nlet a=1\n// about b\nlet b=2\n"
    )

let expectFormatSourceWithoutTriviaIsPlainFormatting unit = assertFormattedSource("let a = 1\n\nlet b = 2\n")("let a=1\nlet b=2")

// A closing `in` can merge onto the same physical line as the expression it introduces
// (`in h + g`) once formatted, even when the original source had it on its own line — a purely
// stylistic wrapping choice, not a semantic difference. Before `lineSignature` normalized away a
// leading, non-solitary `in`, the two forms hashed completely differently and this comment fell
// back to the top of the file instead of staying anchored to the `h + g` line it precedes.
let expectFormatSourceKeepsCommentNearAMergedInLine unit =
    assertFormattedSource(
        "let g = 1\nin let h = 2\n// comment before final\nin h + g\n",
        "let g = 1\nin\nlet h = 2\nin\n// comment before final\nh + g\n"
    )

let expectFormatSourceReportsParseFailures unit =
    match formatSource("let a = \n") with
        | Error(SourceParseFailure(_ :: _)) -> Unit
        | Error(error) -> test.fail("a parse failure must carry diagnostics, got " + Ashes.Trait.Show.show(error))
        | Ok(_) -> test.fail("an unparsable body must be rejected")

let runSourceFormattingTests unit =
    unit
    |> expectSplitSourceLinesDropsTheTerminalNewlineAndCarriageReturns
    |> expectCommentFollowsItsNextAnchor
    |> expectCommentFallsBackToThePreviousAnchor
    |> expectCommentWithoutAnchorsGoesToTheTop
    |> expectRepeatedLinesAnchorByOccurrence
    |> expectReinsertionJoinsWithTheRequestedLineEnding
    |> expectCommentTextIsPreservedVerbatim
    |> expectLeadingCommentsAreSplitFromTheBody
    |> expectImportsAreRenderedCanonically
    |> expectMalformedImportLinesAreRejected
    |> expectFormatSourceKeepsHeaderImportsAndComments
    |> expectFormatSourceWithoutTriviaIsPlainFormatting
    |> expectFormatSourceKeepsCommentNearAMergedInLine
    |> expectFormatSourceReportsParseFailures

let run unit =
    unit
    |> runSourceFormattingTests
    |> (given (_) -> assertProgram("let a = 1\n\nlet b = 2\n")("let a=1\nlet b=2"))
    |> (given (_) ->
        assertProgram(
            "export (\n    value run,\n    type Result(..),\n    module Internal,\n)\n\ntype alias Identity(a) = a\n\ntype UserId = UserId(Int)\n    deriving {Eq}\n\ntype Result(e, a) =\n    | Ok(a)\n    | Error(e)\n\ntype Point =\n    | x: Int\n    | y: Int\n\nlet run value = value\n\nrun(42)\n",
            "export(value run,type Result(..),module Internal)\ntype alias Identity(a)=a\ntype UserId=UserId(Int) deriving {Eq}\ntype Result(e,a)=|Ok(a)|Error(e)\ntype Point=|x:Int|y:Int\nlet run value=value\nrun(42)"
        ))
    |> (given (_) ->
        assertProgram(
            "external type Handle resource destructor closeHandle\nexternal read(borrow *UInt8, consume FfiBuffer(UInt8), out Int) -> FfiStr(nullable owned freeText) needs {Clock} = \"read_native\"\n\n0\n",
            "external type Handle resource destructor closeHandle\nexternal read(borrow *UInt8,consume FfiBuffer(UInt8),out Int)->FfiStr(nullable owned freeText) needs {Clock}=\"read_native\"\n0"
        ))
    |> (given (_) ->
        assertProgram(
            "capability State(a) =\n    | get : Unit -> a\n    | put : a -> Unit\n\nprovide State(Int) =\n    | get = 0\n    | put =\n        given (value) -> Unit\n\ntrait Display(a) requires {Eq(a)} =\n    | display : a -> Str\n    | fallback : a -> Str =\n        given (value) -> \"?\"\n\nimplement Display(Int) =\n    | display =\n        given (value) -> \"int\"\n\n0\n",
            "capability State(a)=|get:Unit->a|put:a->Unit\nprovide State(Int)=|get=0|put=given value->Unit\ntrait Display(a) requires {Eq(a)}=|display:a->Str|fallback:a->Str=given value->\"?\"\nimplement Display(Int)=|display=given value->\"int\"\n0"
        ))
    |> (given (_) ->
        assertProgram(
            "let recursive even n = odd n\nand odd n = even n\n",
            "let recursive even n=odd n\nand odd n=even n"
        ))
    |> (given (_) ->
        assertProgram(
            "let compare : a -> a -> Bool requires {Eq(a), Show(a)} =\n    given (left) ->\n        given (right) -> true\n",
            "let compare : a->a->Bool requires {Show(a),Eq(a)}=given(left,right)->true"
        ))
    |> (given (_) -> assertExpression("1 + 2 * 3\n")("1+2*3"))
    |> (given (_) -> assertExpression("(1 + 2) * 3\n")("(1+2)*3"))
    |> (given (_) -> assertExpression("map transform values\n")("map transform values"))
    |> (given (_) ->
        assertExpression(
            "if ready\nthen run(Unit)\nelse stop(Unit)\n",
            "if ready then run(Unit) else stop(Unit)"
        ))
    |> (given (_) ->
        assertExpression(
            "given (left) ->\n    given (right) -> left + right\n",
            "given (left, right)->left+right"
        ))
    |> (given (_) -> assertExpression("let add x y = x + y\nin add(1)(2)\n")("let add x y=x+y in add(1)(2)"))
    |> (given (_) ->
        assertExpression(
            "outer(\n    \"first\",\n    inner(\n        1,\n        2\n    ),\n    []\n)\n",
            "outer(\n\"first\",\ninner(\n1,\n2\n),\n[]\n)"
        ))
    |> (given (_) -> assertExpression("f(\n    value\n)\n")("f(\nvalue\n)"))
    |> (given (_) ->
        assertExpression(
            multilineRecordExpected,
            multilineRecordSource
        ))
    |> (given (_) ->
        assertExpression(
            "[\n    first,\n    [\n        second,\n        third\n    ],\n    f(\n        fourth,\n        fifth\n    )\n]\n",
            "[\nfirst,\n[\nsecond,\nthird\n],\nf(\nfourth,\nfifth\n)\n]"
        ))
    |> (given (_) -> assertExpression("[\n    value\n]\n")("[\nvalue\n]"))
    |> (given (_) -> assertExpression("18446744073709551615u64\n")("18446744073709551615u64"))
    |> (given (_) -> assertExpression("point with x = 5, y = 6\n")("point with x=5,y=6"))
    |> (given (_) -> assertExpression("Value(state = (inner with currentSpan = previous), temp = temp)\n")("Value(state=(inner with currentSpan=previous), temp=temp)"))
    |> (given (_) -> assertIdempotent("Value(state=(inner with currentSpan=previous), temp=temp)"))
    |> (given (_) -> assertExpression("Value(\n    state = (inner with currentSpan = previous),\n    temp = temp\n)\n")("Value(\nstate=(inner with currentSpan=previous),\ntemp=temp\n)"))
    |> (given (_) -> assertExpression("Value(state = (given (current) -> current with span = next), temp = temp)\n")("Value(state=(given (current) -> current with span=next), temp=temp)"))
    |> (given (_) -> assertExpression("Value(temp = temp, state = inner with currentSpan = previous)\n")("Value(temp=temp, state=inner with currentSpan=previous)"))
    |> (given (_) -> assertExpression("p with a = (q with b = 1), c = 2\n")("p with a=(q with b=1), c=2"))
    |> (given (_) -> assertExpression("f((p with x = 1))\n")("f((p with x=1))"))
    |> (given (_) -> assertExpression("((p with x = 1), [(q with y = 2)])\n")("((p with x=1), [(q with y=2)])"))
    |> (given (_) -> assertExpression("match (p with x = 1) with\n    | _ -> 0\n")("match (p with x=1) with | _ -> 0"))
    |> (given (_) ->
        "a -> List(a) needs {ConsoleIO | e}"
        |> typeFrom
        |> formatTypeExpression
        |> test.assertEqual("a -> List(a) needs {ConsoleIO | e}"))
    |> (given (_) ->
        "A -> (B -> C) needs {Log}"
        |> typeFrom
        |> formatTypeExpression
        |> test.assertEqual("A -> (B -> C) needs {Log}"))
    |> (given (_) ->
        "match value with | Some(head :: tail) as items | None -> items"
        |> patternFrom
        |> assertPattern("Some(head :: tail) as items | None"))
    |> (given (_) -> assertIdempotent("match value with | Some(x) when x > 0 -> x | None -> 0"))
    |> (given (_) ->
        assertExpressionWithOptions(
            "outer(\n    \"first\",\n    inner(\n        1,\n        2\n    ),\n    []\n)\n",
            formattingOptionsDefault,
            "outer(\n\"first\",\ninner(\n1,\n2\n),\n[]\n)"
        ))
    |> (given (_) ->
        assertExpressionWithOptions(
            "outer(\n  \"first\",\n  inner(\n    1,\n    2\n  ),\n  []\n)\n",
            FormattingOptions(indentSize = 2, useTabs = false, newLine = "\n"),
            "outer(\n\"first\",\ninner(\n1,\n2\n),\n[]\n)"
        ))
    |> (given (_) ->
        assertExpressionWithOptions(
            "outer(\n\t\"first\",\n\tinner(\n\t\t1,\n\t\t2\n\t),\n\t[]\n)\n",
            FormattingOptions(indentSize = 4, useTabs = true, newLine = "\n"),
            "outer(\n\"first\",\ninner(\n1,\n2\n),\n[]\n)"
        ))
    |> (given (_) ->
        assertExpressionWithOptions(
            "outer(\r\n    \"first\",\r\n    inner(\r\n        1,\r\n        2\r\n    ),\r\n    []\r\n)\r\n",
            FormattingOptions(indentSize = 4, useTabs = false, newLine = "\r\n"),
            "outer(\n\"first\",\ninner(\n1,\n2\n),\n[]\n)"
        ))
    |> (given (_) ->
        assertExpressionWithOptions(
            "given (left) ->\n    given (right) -> left + right\n",
            FormattingOptions(indentSize = 0, useTabs = false, newLine = "not-a-newline"),
            "given (left, right)->left+right"
        ))
    |> (given (_) ->
        "outer(\n\"first\",\ninner(\n1,\n2\n),\n[]\n)"
        |> expressionFrom
        |> formatExpression
        |> test.assertEqual(formatExpressionWithOptions(expressionFrom("outer(\n\"first\",\ninner(\n1,\n2\n),\n[]\n)"))(false)(formattingOptionsDefault)))
    |> (given (_) -> assertExpressionWithPipelines("x\n|> f\n|> g\n")("g(f(x))"))
    |> (given (_) -> assertExpressionWithPipelines("x\n|> f\n|> g\n|> h\n")("h(g(f(x)))"))
    |> (given (_) -> assertExpressionWithPipelines("f(x)\n")("f(x)"))
    |> (given (_) -> assertExpressionWithPipelines("x\n|> f\n|> g\n")("x |> f |> g"))
    |> (given (_) -> assertExpressionWithPipelines("h(x\n|> f\n|> Some)\n")("h(Some(f(x)))"))
    |> (given (_) -> assertExpressionWithPipelines("f(Some(x))\n")("f(Some(x))"))
    |> (given (_) -> assertExpressionWithPipelines("x\n|?> f\n|?> g\n")("x |?> f |?> g"))
    |> (given (_) -> assertExpressionWithPipelines("x\n|!> f\n|!> g\n")("x |!> f |!> g"))
    |> (given (_) -> assertExpressionWithPipelines("x\n|> f\n|> g\n|?> h\n")("g(f(x)) |?> h"))
    |> (given (_) ->
        assertExpressionWithPipelines(
            "if cond\nthen\n    x\n    |> f\n    |> g\nelse h(x)\n",
            "if cond then g(f(x)) else h(x)"
        ))
    |> (given (_) ->
        assertExpressionWithPipelines(
            "given (x) ->\n    x\n    |> f\n    |> g\n    |> h\n",
            "given (x) -> h(g(f(x)))"
        ))
    |> (given (_) ->
        assertExpressionWithPipelines(
            "match v with\n    | x ->\n        x\n        |> f\n        |> g\n        |> h\n",
            "match v with | x -> h(g(f(x)))"
        ))
    |> (given (_) ->
        assertExpressionWithPipelines(
            "let y =\n    x\n    |> f\n    |> g\n    |> h\nin y\n",
            "let y = h(g(f(x))) in y"
        ))
    |> (given (_) -> assertExpressionWithPipelines("y\n|> f(x)\n|> g\n")("g(f(x, y))"))
    |> (given (_) -> assertExpressionWithPipelines("y\n|> f(x)\n|> g\n")("g(f(x)(y))"))
    |> (given (_) -> Ashes.IO.print("all self-hosted formatter tests passed"))

run(Unit)
