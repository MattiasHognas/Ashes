import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ProgramInference
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
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("program should infer: " + Ashes.Trait.Show.show(error))

let pairAlias unit = TypeAliasDecl(name = "Pair", typeParameters = [TypeParameter(name = "a")], target = TypeTuple([TypeNamed("a"), TypeNamed("a")]))

let userIdType unit = ZeroCostTypeDecl(name = "UserId", typeParameters = [], constructor = TypeConstructor(name = "UserId", parameters = [TypeNamed("Int")], fieldNames = []), derivingTraits = [])

let optionType unit = TypeDecl(name = "Option", typeParameters = [TypeParameter(name = "a")], constructors = [TypeConstructor(name = "None", parameters = [], fieldNames = []), TypeConstructor(name = "Some", parameters = [TypeNamed("a")], fieldNames = [])], isRecord = false, derivingTraits = [])

let pointType unit = TypeDecl(name = "Point", typeParameters = [], constructors = [TypeConstructor(name = "Point", parameters = [TypeNamed("Int"), TypeNamed("Str")], fieldNames = ["x", "label"])], isRecord = true, derivingTraits = [])

let pointValue unit =
    ExprCall(ExprCall(ExprVar("Point"))(ExprInt(1))(false))(ExprString("origin"))(false)

let expectSequentialBindings unit =
    (let identity = LetBindingSyntax(name = "identity", value = ExprLambda("value")(ExprVar("value"))(None), sugarParameters = [], typeAnnotation = None, requirements = [])
    in
        let answer = LetBindingSyntax(name = "answer", value = ExprCall(ExprVar("identity"))(ExprInt(42))(false), sugarParameters = [], typeAnnotation = None, requirements = [])
        in expectProgramType("Int")(ProgramSyntax(items = [TopLevelLet(identity)(false), TopLevelLet(answer)(false)], body = Some(ExprVar("answer")))))

let expectRecursiveBinding unit =
    (let loop =
        LetBindingSyntax(name = "loop", value = ExprLambda("value")(false
        |> ExprCall(ExprVar("loop"))(ExprVar("value"))
        |> ExprIf(ExprBool(true))(ExprVar("value")))(None), sugarParameters = [], typeAnnotation = None, requirements = [])
    in
        expectProgramType("Int")(ProgramSyntax(items = [TopLevelLet(loop)(true)], body = false
        |> ExprCall(ExprVar("loop"))(ExprInt(1))
        |> Some)))

let expectMutualBindings unit =
    (let first =
        LetBindingSyntax(name = "first", value = ExprLambda("value")(ExprCall(ExprVar("second"))(ExprVar("value"))(false))(None), sugarParameters = [], typeAnnotation = None, requirements = [])
    in
        let second = LetBindingSyntax(name = "second", value = ExprLambda("value")(ExprVar("value"))(None), sugarParameters = [], typeAnnotation = None, requirements = [])
        in
            let uses = ExprTuple([ExprCall(ExprVar("first"))(ExprInt(1))(false), ExprCall(ExprVar("first"))(ExprString("value"))(false)])
            in expectProgramType("(Int, Str)")(ProgramSyntax(items = [TopLevelRecursiveGroup([first, second])], body = Some(uses))))

let expectTypeAlias unit =
    (let pair =
        LetBindingSyntax(name = "pair", value = ExprTuple([ExprInt(1), ExprInt(2)]), sugarParameters = [], typeAnnotation = [TypeNamed("Int")]
        |> TypeApplied("Pair")
        |> Some, requirements = [])
    in
        expectProgramType("(Int, Int)")(ProgramSyntax(items = [Unit
        |> pairAlias
        |> TopLevelTypeAlias, TopLevelLet(pair)(false)], body = Some(ExprVar("pair")))))

let expectZeroCostType unit =
    unit
    |> (given (_) ->
        expectProgramType("UserId")(ProgramSyntax(items = [Unit
        |> userIdType
        |> TopLevelZeroCostType], body = false
        |> ExprCall(ExprVar("UserId"))(ExprInt(7))
        |> Some)))
    |> (given (_) ->
        expectProgramType("Int")(ProgramSyntax(items = [Unit
        |> userIdType
        |> TopLevelZeroCostType], body = None
        |> ExprMatch(ExprCall(ExprVar("UserId"))(ExprInt(7))(false))([(PatternConstructor("UserId")([PatternVar("value")]), ExprVar("value"), None)])
        |> Some)))

let rejectWrongAliasArity unit =
    (let binding =
        LetBindingSyntax(name = "badPair", value = ExprTuple([]), sugarParameters = [], typeAnnotation = []
        |> TypeApplied("Pair")
        |> Some, requirements = [])
    in
        match inferProgram(ProgramSyntax(items = [Unit
        |> pairAlias
        |> TopLevelTypeAlias, TopLevelLet(binding)(false)], body = None)) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceTypeResolutionError(TypeNameArityMismatch("Pair", 1, 0)))) } -> Unit
            | _ -> test.fail("type alias arity should be checked"))

let rejectPolymorphicRecursion unit =
    (let body = ExprLambda("value")(ExprTuple([ExprCall(ExprVar("bad"))(ExprInt(1))(false), ExprCall(ExprVar("bad"))(ExprString("value"))(false)]))(None)
    in
        let binding = LetBindingSyntax(name = "bad", value = body, sugarParameters = [], typeAnnotation = None, requirements = [])
        in
            match inferProgram(ProgramSyntax(items = [TopLevelLet(binding)(true)], body = None)) with
                | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
                | _ -> test.fail("recursive bindings should remain monomorphic inside their group"))

let expectConstructors unit =
    unit
    |> (given (_) ->
        expectProgramType("Option(Str)")(ProgramSyntax(items = [Unit
        |> optionType
        |> TopLevelType], body = false
        |> ExprCall(ExprVar("Some"))(ExprString("value"))
        |> Some)))
    |> (given (_) ->
        expectProgramType("Str")(ProgramSyntax(items = [Unit
        |> optionType
        |> TopLevelType], body = None
        |> ExprMatch(ExprCall(ExprVar("Some"))(ExprString("value"))(false))([(PatternConstructor("Some")([PatternVar("value")]), ExprVar("value"), None), (PatternConstructor("None")([]), ExprString("none"), None)])
        |> Some)))

let expectRecordPattern unit =
    (let body =
        ExprMatch(pointValue(Unit))([(PatternRecord("Point")([("label", PatternVar("label"))]), ExprVar("label"), None)])(None)
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
        ExprMatch(ExprCall(ExprVar("Some"))(ExprInt(1))(false))([(PatternConstructor("Some")([]), ExprInt(0), None)])(None)
    in
        match inferProgram(ProgramSyntax(items = [Unit
        |> optionType
        |> TopLevelType], body = Some(body))) with
            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(ConstructorPatternArityMismatch("Some"))) } -> Unit
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
    |> (given (_) -> Ashes.IO.print("all self-hosted type declaration inference tests passed"))
