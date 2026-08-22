import Ashes.Test as test
import AshesCompiler.Frontend.InlineModules
let assertNamed name expected actual =
    if expected == actual
    then Unit
    else
        test.fail(
            name + " expected " + Ashes.Trait.Show.show(expected) + " but got " + Ashes.Trait.Show.show(actual)
        )

let expectExpansion (result: Result(InlineModuleError, InlineModuleExpansion)) =
    match result with
        | Ok(expansion) -> expansion
        | Error(error) -> test.fail("unexpected inline-module error: " + Ashes.Trait.Show.show(error))

let expectError (expected: InlineModuleError) (result: Result(InlineModuleError, InlineModuleExpansion)) =
    match result with
        | Ok(_) -> test.fail("expected inline-module error")
        | Error(actual) -> assertNamed("inline-module error")(expected)(actual)

let sourceLines lines = Ashes.Text.join("\n")(lines)

let moduleInfo name source = InlineModuleInfo(name = name, source = source)

let inlineExpansion source modules = InlineModuleExpansion(source = source, modules = modules)

let checkHeaderRecognition unit =
    "let value = 1\nmodule Nested = // retained by the scanner\n    let answer = 42"
    |> containsInlineModule
    |> assertNamed("valid header")(true)
    |> (given (_) ->
        "module nested =\n    let answer = 42"
        |> containsInlineModule
        |> assertNamed("lowercase header")(false))
    |> (given (_) ->
        "module Nested = value"
        |> containsInlineModule
        |> assertNamed("header suffix")(false))

let checkFlatExpansion unit =
    "module Math = // lifted\n    let answer = 42\nlet result = Math.answer"
    |> expandInlineModules("Example")
    |> expectExpansion
    |> assertNamed("flat expansion")(
        inlineExpansion(
            "let result = Example.Math.answer",
            [moduleInfo("Example.Math")("let answer = 42")]
        )
    )

let checkEntryExpansion unit =
    "module Math =\n    let answer = 42\nMath.answer"
    |> expandInlineModules("")
    |> expectExpansion
    |> assertNamed("entry expansion")(
        inlineExpansion(
            "Math.answer",
            [moduleInfo("Math")("let answer = 42")]
        )
    )

let checkNestedExpansion unit =
    [
        "module Outer =",
        "    module Inner =",
        "        let answer = 42",
        "    let answer = Inner.answer",
        "let result = Outer.answer"
    ]
    |> sourceLines
    |> expandInlineModules("Example")
    |> expectExpansion
    |> assertNamed("nested expansion")(
        inlineExpansion(
            "let result = Example.Outer.answer",
            [
                moduleInfo("Example.Outer.Inner")("let answer = 42"),
                moduleInfo("Example.Outer")("let answer = Example.Outer.Inner.answer")
            ]
        )
    )

let checkQualifierExpansion (expansion: InlineModuleExpansion) =
    match expansion with
        | InlineModuleExpansion { source = source, modules = _modules } ->
            assertNamed("qualifier boundaries")(
                "let direct = Example.Math.answer\nlet deeper = Existing.Math.answer\nlet text = \"Math.answer\"",
                source
            )

let checkQualifierBoundaries unit =
    [
        "module Math =",
        "    let answer = 42",
        "let direct = Math.answer",
        "let deeper = Existing.Math.answer",
        "let text = \"Math.answer\""
    ]
    |> sourceLines
    |> expandInlineModules("Example")
    |> expectExpansion
    |> checkQualifierExpansion

let checkDuplicateName unit =
    "module Same =\n    let first = 1\nmodule Same =\n    let second = 2"
    |> expandInlineModules("Example")
    |> expectError(DuplicateInlineModule("Example")("Same"))

let checkReservedName unit =
    "module Ashes =\n    let answer = 42"
    |> expandInlineModules("Example")
    |> expectError(ReservedInlineModule("Example.Ashes"))

let checkRestrictedBody unit =
    "module Nested =\n    import Other\n    let answer = 42"
    |> expandInlineModules("Example")
    |> expectError(InlineModuleImport("Example.Nested"))
    |> (given (_) ->
        "module Nested =\n    external native() -> Int = \"native\"\n    let answer = 42"
        |> expandInlineModules("Example")
        |> expectError(InlineModuleExternal("Example.Nested")))
    |> (given (_) ->
        "module Nested =\n    let answer = 42\n    answer"
        |> expandInlineModules("Example")
        |> expectError(InlineModuleTrailingExpression("Example.Nested")))

let checkMalformedExpansion (expansion: InlineModuleExpansion) =
    match expansion with
        | InlineModuleExpansion { source = _source, modules = modules } ->
            assertNamed("malformed body")(
                [moduleInfo("Example.Nested")("let answer =")],
                modules
            )

let checkMalformedBodyDefersToParser unit =
    "module Nested =\n    let answer ="
    |> expandInlineModules("Example")
    |> expectExpansion
    |> checkMalformedExpansion

let run unit =
    unit
    |> checkHeaderRecognition
    |> checkFlatExpansion
    |> checkEntryExpansion
    |> checkNestedExpansion
    |> checkQualifierBoundaries
    |> checkDuplicateName
    |> checkReservedName
    |> checkRestrictedBody
    |> checkMalformedBodyDefersToParser
