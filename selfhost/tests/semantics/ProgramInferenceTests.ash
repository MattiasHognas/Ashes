import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.RequirementSites.requirementSiteSpan
import AshesCompiler.Semantics.RequirementSites.requirementSiteWithContext
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.Unification
let expectProgramType expected program =
    match inferProgram(program) with
        | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
            let actual =
                semanticType
                |> applySubstitution(substitution)
                |> formatSemanticType
            in
                if actual == expected
                then Unit
                else test.fail("expected program type " + expected + " but inferred " + actual)
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } ->
            test.fail(
                "program should infer: " + Ashes.Trait.Show.show(error)
            )

let pairAlias unit =
    TypeAliasDecl(name = "Pair", typeParameters = [TypeParameter(name = "a")], target = TypeTuple(
        [TypeNamed("a"), TypeNamed("a")]
    ))

let userIdType unit =
    ZeroCostTypeDecl(name = "UserId", typeParameters = [], constructor = TypeConstructor(name = "UserId", parameters = [TypeNamed(
        "Int"
    )], fieldNames = []), derivingTraits = [])

let optionType unit =
    TypeDecl(name = "Option", typeParameters = [TypeParameter(name = "a")], constructors = [TypeConstructor(name = "None", parameters = [], fieldNames = []), TypeConstructor(name = "Some", parameters = [TypeNamed(
        "a"
    )], fieldNames = [])], isRecord = false, derivingTraits = [])

let pointType unit =
    TypeDecl(name = "Point", typeParameters = [], constructors = [TypeConstructor(name = "Point", parameters = [TypeNamed(
        "Int"
    ), TypeNamed(
        "Str"
    )], fieldNames = ["x", "label"])], isRecord = true, derivingTraits = [])

let pointValue unit =
    ExprCall(
        ExprCall(ExprVar("Point"))(ExprInt(1))(false)(callArgumentsInline),
        ExprString("origin"),
        false,
        callArgumentsInline
    )

let expectSequentialBindings unit =
    (let identity =
        LetBindingSyntax(name = "identity", value = ExprLambda(
            "value",
            ExprVar("value"),
            None
        ), sugarParameters = [], typeAnnotation = None, requirements = [], declarationStart = -1)
    in
        let answer =
            LetBindingSyntax(name = "answer", value = ExprCall(
                ExprVar("identity"),
                ExprInt(42),
                false,
                callArgumentsInline
            ), sugarParameters = [], typeAnnotation = None, requirements = [], declarationStart = -1)
        in
            expectProgramType(
                "Int",
                ProgramSyntax(items = [TopLevelLet(identity)(false), TopLevelLet(answer)(false)], body = Some(
                    ExprVar("answer")
                ))
            ))

let expectRecursiveBinding unit =
    (let loop =
        LetBindingSyntax(name = "loop", value = ExprLambda("value")(callArgumentsInline
        |> ExprCall(ExprVar("loop"))(ExprVar("value"))(false)
        |> ExprIf(
            ExprBool(true),
            ExprVar("value")
        ))(None), sugarParameters = [], typeAnnotation = None, requirements = [], declarationStart = -1)
    in
        expectProgramType("Int")(ProgramSyntax(items = [TopLevelLet(loop)(true)], body = callArgumentsInline
        |> ExprCall(ExprVar("loop"))(ExprInt(1))(false)
        |> Some)))

let expectMutualBindings unit =
    (let first =
        LetBindingSyntax(name = "first", value = ExprLambda(
            "value",
            ExprCall(ExprVar("second"))(ExprVar("value"))(false)(callArgumentsInline),
            None
        ), sugarParameters = [], typeAnnotation = None, requirements = [], declarationStart = -1)
    in
        let second =
            LetBindingSyntax(name = "second", value = ExprLambda(
                "value",
                ExprVar("value"),
                None
            ), sugarParameters = [], typeAnnotation = None, requirements = [], declarationStart = -1)
        in
            let uses =
                ExprTuple(
                    [ExprCall(ExprVar("first"))(ExprInt(1))(false)(callArgumentsInline), ExprCall(
                        ExprVar("first"),
                        ExprString("value"),
                        false,
                        callArgumentsInline
                    )]
                )
            in
                expectProgramType(
                    "(Int, Str)",
                    ProgramSyntax(items = [TopLevelRecursiveGroup([first, second])], body = Some(uses))
                ))

let expectTypeAlias unit =
    (let pair =
        LetBindingSyntax(name = "pair", value = ExprTuple(
            [ExprInt(1), ExprInt(2)]
        ), sugarParameters = [], typeAnnotation = [TypeNamed("Int")]
        |> TypeApplied("Pair")
        |> Some, requirements = [], declarationStart = -1)
    in
        expectProgramType("(Int, Int)")(ProgramSyntax(items = [Unit
        |> pairAlias
        |> TopLevelTypeAlias, TopLevelLet(pair)(false)], body = Some(ExprVar("pair")))))

let expectZeroCostType unit =
    unit
    |> (given (_) ->
        expectProgramType("UserId")(ProgramSyntax(items = [Unit
        |> userIdType
        |> TopLevelZeroCostType], body = callArgumentsInline
        |> ExprCall(ExprVar("UserId"))(ExprInt(7))(false)
        |> Some)))
    |> (given (_) ->
        expectProgramType("Int")(ProgramSyntax(items = [Unit
        |> userIdType
        |> TopLevelZeroCostType], body = None
        |> ExprMatch(
            ExprCall(ExprVar("UserId"))(ExprInt(7))(false)(callArgumentsInline),
            [(PatternConstructor("UserId")([PatternVar("value")]), ExprVar("value"), None)]
        )
        |> Some)))

let rejectWrongAliasArity unit =
    (let binding =
        LetBindingSyntax(name = "badPair", value = ExprTuple([]), sugarParameters = [], typeAnnotation = []
        |> TypeApplied("Pair")
        |> Some, requirements = [], declarationStart = -1)
    in
        match inferProgram(ProgramSyntax(items = [Unit
        |> pairAlias
        |> TopLevelTypeAlias, TopLevelLet(binding)(false)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceTypeResolutionError(TypeNameArityMismatch("Pair", 1, 0)))) } -> Unit
            | _ -> test.fail("type alias arity should be checked"))

// The second call meets the group's monomorphic `bad`, now `Int -> ...`, with a Str: the mismatch
// is reported as the call's argument mismatch, as stage 0 words it ("in argument #1 of call to
// 'bad'").
let rejectPolymorphicRecursion unit =
    (let body =
        ExprLambda(
            "value",
            ExprTuple(
                [ExprCall(ExprVar("bad"))(ExprInt(1))(false)(callArgumentsInline), ExprCall(
                    ExprVar("bad"),
                    ExprString("value"),
                    false,
                    callArgumentsInline
                )]
            ),
            None
        )
    in
        let binding = LetBindingSyntax(name = "bad", value = body, sugarParameters = [], typeAnnotation = None, requirements = [], declarationStart = -1)
        in
            match inferProgram(ProgramSyntax(items = [TopLevelLet(binding)(true)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(CallArgumentMismatch(TypeMismatch(_left, _right), 1, Some("bad")))) } -> Unit
                | _ -> test.fail("recursive bindings should remain monomorphic inside their group"))

let expectConstructors unit =
    unit
    |> (given (_) ->
        expectProgramType("Option(Str)")(ProgramSyntax(items = [Unit
        |> optionType
        |> TopLevelType], body = callArgumentsInline
        |> ExprCall(ExprVar("Some"))(ExprString("value"))(false)
        |> Some)))
    |> (given (_) ->
        expectProgramType("Str")(ProgramSyntax(items = [Unit
        |> optionType
        |> TopLevelType], body = None
        |> ExprMatch(
            ExprCall(ExprVar("Some"))(ExprString("value"))(false)(callArgumentsInline),
            [(PatternConstructor(
                "Some",
                [PatternVar("value")]
            ), ExprVar("value"), None), (PatternConstructor("None")([]), ExprString("none"), None)]
        )
        |> Some)))

let expectRecordPattern unit =
    (let body =
        ExprMatch(
            pointValue(Unit),
            [(PatternRecord("Point")([("label", PatternVar("label"))]), ExprVar("label"), None)],
            None
        )
    in
        expectProgramType("Str")(ProgramSyntax(items = [Unit
        |> pointType
        |> TopLevelType], body = Some(body))))

let expectOrPattern unit =
    (let alias = PatternOr([PatternAs(PatternBool(true))("value"), PatternAs(PatternBool(false))("value")])
    in
        expectProgramType("Bool")(ProgramSyntax(items = [], body = None
        |> ExprMatch(ExprBool(true))([(alias, ExprVar("value"), None)])
        |> Some)))

let rejectWrongConstructorPatternArity unit =
    (let body =
        ExprMatch(
            ExprCall(ExprVar("Some"))(ExprInt(1))(false)(callArgumentsInline),
            [(PatternConstructor("Some")([]), ExprInt(0), None)],
            None
        )
    in
        match inferProgram(ProgramSyntax(items = [Unit
        |> optionType
        |> TopLevelType], body = Some(body))) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(ConstructorPatternArityMismatch("Some", 1, 0))) } -> Unit
            | _ -> test.fail("constructor pattern arity should be checked"))

let rejectUnknownRecordPatternField unit =
    (let body =
        ExprMatch(pointValue(Unit))([(PatternRecord("Point")([("missing", PatternWildcard)]), ExprInt(0), None)])(None)
    in
        match inferProgram(ProgramSyntax(items = [Unit
        |> pointType
        |> TopLevelType], body = Some(body))) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(UnknownRecordPatternField("Point", "missing"))) } -> Unit
            | _ -> test.fail("record pattern fields should be checked"))

let rejectInconsistentOrPattern unit =
    (let pattern = PatternOr([PatternVar("value"), PatternWildcard])
    in
        match inferProgram(ProgramSyntax(items = [], body = None
        |> ExprMatch(ExprBool(true))([(pattern, ExprBool(true), None)])
        |> Some)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InconsistentOrPatternBindings)) } -> Unit
            | _ -> test.fail("or-pattern alternatives should bind the same names"))

// The start of the span the inference located the rejection of `source` at.
let rejectionStart source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } ->
            match inferProgram(program) with
                | ProgramInferenceResult { error = Some(_error), errorLocation = Some(InferenceErrorLocation { span = Some(TextSpan { start = start }) }) } -> start
                | ProgramInferenceResult { error = error, errorLocation = location } -> test.fail("expected a located rejection: " + Ashes.Trait.Show.show(error) + " at " + Ashes.Trait.Show.show(location))
        | _ -> test.fail("position fixture should parse")

// Stage 0's positions: a constructor call's arity at its first argument, a constructor
// pattern's at its first argument pattern, an unreachable arm at its pattern (an or-pattern's
// redundant alternative), an unknown constructor pattern at itself.
let expectPatternAndArityPositions unit =
    (let boxType = "type Box =\n    | Box(Int)\n"
    in
        Unit
        |> (given (_) ->
            boxType + "Box(1)(2)"
            |> rejectionStart
            |> test.assertEqual(Ashes.Text.length(boxType) + 4))
        |> (given (_) ->
            boxType + "match Box(1) with\n    | Box(a, b) -> a"
            |> rejectionStart
            |> test.assertEqual(Ashes.Text.length(boxType) + 28))
        |> (given (_) ->
            "match 1 with\n    | 1 | 1 -> 1\n    | _ -> 0"
            |> rejectionStart
            |> test.assertEqual(23))
        |> (given (_) ->
            "match 1 with\n    | Nope(x) -> 1\n    | _ -> 0"
            |> rejectionStart
            |> test.assertEqual(19)))

let siteStart traitName source =
    match parseExpression(source) with
        | ExpressionParseResult { expression = expression, diagnostics = [] } ->
            match requirementSiteSpan(traitName)(expression) with
                | Some(TextSpan { start = start }) -> start
                | None -> -1
        | _ -> test.fail("requirement site fixture should parse")

let siteContext traitName source =
    match parseExpression(source) with
        | ExpressionParseResult { expression = expression, diagnostics = [] } ->
            match requirementSiteWithContext(traitName)(expression) with
                | Some((_span, Some(context))) -> context
                | _ -> ""
        | _ -> test.fail("requirement context fixture should parse")

// A requirement is located at the operator or `print` argument that raised it; a `let`'s function
// value is not searched, its requirements belonging to the function's own scheme. Stage 0's contexts (an
// else branch, a match arm) are named outermost first, and a stitched trait name is recognised.
let expectRequirementSites unit =
    Unit
    |> (given (_) ->
        "[given (x) -> x + x]"
        |> siteStart("Add")
        |> test.assertEqual(14))
    |> (given (_) ->
        "Ashes.IO.print(1.5)"
        |> siteStart("$PrintArgument")
        |> test.assertEqual(15))
    |> (given (_) ->
        "let recursive invert x = !x in invert"
        |> siteStart("Not")
        |> test.assertEqual(-1))
    |> (given (_) ->
        "if true then Unit else (match 1 with | _ -> Ashes.IO.print(1.5))"
        |> siteContext("$PrintArgument")
        |> test.assertEqual("in if branches -> in match arm 1"))
    |> (given (_) ->
        "Ashes_Trait_Default.default(Unit)"
        |> siteStart("Default")
        |> test.assertEqual(0))

// An alias cycle is reported at the declaration of the alias that reached itself again.
let expectAliasCycleAtDeclaration unit =
    match parseProgram("type alias Other = Int\ntype alias First = Second\ntype alias Second = First\n0") with
        | ProgramParseResult { program = ProgramSyntax { items = items, body = body }, diagnostics = [] } ->
            match inferProgramUnitsFrom(emptyTypeEnvironmentForPackage("app"))([ProgramInferenceUnit(packageId = "app", program = ProgramSyntax(items = items, body = None))])(body)("app") with
                | ProgramInferenceResult { error = Some(RecursiveTypeAlias(_cycle)), errorLocation = Some(InferenceErrorLocation { declarationSpan = Some(TextSpan { start = 23 }) }) } -> Unit
                | ProgramInferenceResult { error = error, errorLocation = location } -> test.fail("alias cycle should be located at its declaration: " + Ashes.Trait.Show.show(error) + " at " + Ashes.Trait.Show.show(location))
        | _ -> test.fail("alias cycle fixture should parse")

let runProgramInferenceTests unit =
    unit
    |> expectSequentialBindings
    |> expectRecursiveBinding
    |> expectMutualBindings
    |> expectTypeAlias
    |> expectZeroCostType
    |> rejectWrongAliasArity
    |> rejectPolymorphicRecursion
    |> expectConstructors
    |> expectRecordPattern
    |> expectOrPattern
    |> rejectWrongConstructorPatternArity
    |> rejectUnknownRecordPatternField
    |> rejectInconsistentOrPattern
    |> expectPatternAndArityPositions
    |> expectRequirementSites
    |> expectAliasCycleAtDeclaration
    |> (given (_) -> Ashes.IO.print("all self-hosted type declaration inference tests passed"))
