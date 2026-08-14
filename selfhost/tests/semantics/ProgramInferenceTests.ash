import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ProgramInference
let expectProgramType expected program =
    match inferProgram(program) with
        | ProgramInferenceResult { semanticType = semanticType, substitution = substitution, environment = _environment, error = None } ->
            let actual = formatSemanticType(applySubstitution(substitution)(semanticType))
            in
                if actual == expected
                then Unit
                else test.fail("expected program type " + expected + " but inferred " + actual)
        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(error) } -> test.fail("program should infer: " + Ashes.Trait.Show.show(error))

let runProgramInferenceTests unit =
    (let identity = LetBindingSyntax(name = "identity", value = ExprLambda("value")(ExprVar("value"))(None), sugarParameters = [], typeAnnotation = None, requirements = [])
    in
        let answer = LetBindingSyntax(name = "answer", value = ExprCall(ExprVar("identity"))(ExprInt(42))(false), sugarParameters = [], typeAnnotation = None, requirements = [])
        in
            let sequentialProgram = ProgramSyntax(items = [TopLevelLet(identity)(false), TopLevelLet(answer)(false)], body = Some(ExprVar("answer")))
            in
                let sequentialChecked = expectProgramType("Int")(sequentialProgram)
                in
                    let recursiveLoop = LetBindingSyntax(name = "loop", value = ExprLambda("value")(ExprIf(ExprBool(true))(ExprVar("value"))(ExprCall(ExprVar("loop"))(ExprVar("value"))(false)))(None), sugarParameters = [], typeAnnotation = None, requirements = [])
                    in
                        let recursiveChecked = expectProgramType("Int")(ProgramSyntax(items = [TopLevelLet(recursiveLoop)(true)], body = Some(ExprCall(ExprVar("loop"))(ExprInt(1))(false))))
                        in
                            let firstMutual = LetBindingSyntax(name = "first", value = ExprLambda("value")(ExprCall(ExprVar("second"))(ExprVar("value"))(false))(None), sugarParameters = [], typeAnnotation = None, requirements = [])
                            in
                                let secondMutual = LetBindingSyntax(name = "second", value = ExprLambda("value")(ExprVar("value"))(None), sugarParameters = [], typeAnnotation = None, requirements = [])
                                in
                                    let mutualUses = ExprTuple([ExprCall(ExprVar("first"))(ExprInt(1))(false), ExprCall(ExprVar("first"))(ExprString("value"))(false)])
                                    in
                                        let mutualProgram = ProgramSyntax(items = [TopLevelRecursiveGroup([firstMutual, secondMutual])], body = Some(mutualUses))
                                        in
                                            let mutualChecked = expectProgramType("(Int, Str)")(mutualProgram)
                                            in
                                                let pairAlias = TypeAliasDecl(name = "Pair", typeParameters = [TypeParameter(name = "a")], target = TypeTuple([TypeNamed("a"), TypeNamed("a")]))
                                                in
                                                    let pairBinding = LetBindingSyntax(name = "pair", value = ExprTuple([ExprInt(1), ExprInt(2)]), sugarParameters = [], typeAnnotation = Some(TypeApplied("Pair")([TypeNamed("Int")])), requirements = [])
                                                    in
                                                        let aliasChecked = expectProgramType("(Int, Int)")(ProgramSyntax(items = [TopLevelTypeAlias(pairAlias), TopLevelLet(pairBinding)(false)], body = Some(ExprVar("pair"))))
                                                        in
                                                            let userIdConstructor = TypeConstructor(name = "UserId", parameters = [TypeNamed("Int")], fieldNames = [])
                                                            in
                                                                let userIdType = ZeroCostTypeDecl(name = "UserId", typeParameters = [], constructor = userIdConstructor, derivingTraits = [])
                                                                in
                                                                    let zeroCostChecked = expectProgramType("UserId")(ProgramSyntax(items = [TopLevelZeroCostType(userIdType)], body = Some(ExprCall(ExprVar("UserId"))(ExprInt(7))(false))))
                                                                    in
                                                                        let userIdMatch = ExprMatch(ExprCall(ExprVar("UserId"))(ExprInt(7))(false))([(PatternConstructor("UserId")([PatternVar("value")]), ExprVar("value"), None)])(None)
                                                                        in
                                                                            let zeroCostPatternChecked = expectProgramType("Int")(ProgramSyntax(items = [TopLevelZeroCostType(userIdType)], body = Some(userIdMatch)))
                                                                            in
                                                                                let wrongAliasBinding = LetBindingSyntax(name = "badPair", value = ExprTuple([]), sugarParameters = [], typeAnnotation = Some(TypeApplied("Pair")([])), requirements = [])
                                                                                in
                                                                                    let aliasArityChecked =
                                                                                        match inferProgram(ProgramSyntax(items = [TopLevelTypeAlias(pairAlias), TopLevelLet(wrongAliasBinding)(false)], body = None)) with
                                                                                            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceTypeResolutionError(TypeNameArityMismatch("Pair", 1, 0)))) } -> Unit
                                                                                            | _ -> test.fail("type alias arity should be checked")
                                                                                    in
                                                                                        let polymorphicRecursiveBody = ExprLambda("value")(ExprTuple([ExprCall(ExprVar("bad"))(ExprInt(1))(false), ExprCall(ExprVar("bad"))(ExprString("value"))(false)]))(None)
                                                                                        in
                                                                                            let polymorphicRecursive = LetBindingSyntax(name = "bad", value = polymorphicRecursiveBody, sugarParameters = [], typeAnnotation = None, requirements = [])
                                                                                            in
                                                                                                let polymorphicRecursionChecked =
                                                                                                    match inferProgram(ProgramSyntax(items = [TopLevelLet(polymorphicRecursive)(true)], body = None)) with
                                                                                                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(_left, _right)))) } -> Unit
                                                                                                        | _ -> test.fail("recursive bindings should remain monomorphic inside their group")
                                                                                                in
                                                                                                    let optionType = TypeDecl(name = "Option", typeParameters = [TypeParameter(name = "a")], constructors = [TypeConstructor(name = "None", parameters = [], fieldNames = []), TypeConstructor(name = "Some", parameters = [TypeNamed("a")], fieldNames = [])], isRecord = false, derivingTraits = [])
                                                                                                    in
                                                                                                        let constructorProgram = ProgramSyntax(items = [TopLevelType(optionType)], body = Some(ExprCall(ExprVar("Some"))(ExprString("value"))(false)))
                                                                                                        in
                                                                                                            let constructorChecked = expectProgramType("Option(Str)")(constructorProgram)
                                                                                                            in
                                                                                                                let optionMatch = ExprMatch(ExprCall(ExprVar("Some"))(ExprString("value"))(false))([(PatternConstructor("Some")([PatternVar("value")]), ExprVar("value"), None), (PatternConstructor("None")([]), ExprString("none"), None)])(None)
                                                                                                                in
                                                                                                                    let constructorPatternProgram = ProgramSyntax(items = [TopLevelType(optionType)], body = Some(optionMatch))
                                                                                                                    in
                                                                                                                        let constructorPatternChecked = expectProgramType("Str")(constructorPatternProgram)
                                                                                                                        in
                                                                                                                            let pointType = TypeDecl(name = "Point", typeParameters = [], constructors = [TypeConstructor(name = "Point", parameters = [TypeNamed("Int"), TypeNamed("Str")], fieldNames = ["x", "label"])], isRecord = true, derivingTraits = [])
                                                                                                                            in
                                                                                                                                let pointValue = ExprCall(ExprCall(ExprVar("Point"))(ExprInt(1))(false))(ExprString("origin"))(false)
                                                                                                                                in
                                                                                                                                    let recordMatch = ExprMatch(pointValue)([(PatternRecord("Point")([("label", PatternVar("label"))]), ExprVar("label"), None)])(None)
                                                                                                                                    in
                                                                                                                                        let recordPatternChecked = expectProgramType("Str")(ProgramSyntax(items = [TopLevelType(pointType)], body = Some(recordMatch)))
                                                                                                                                        in
                                                                                                                                            let booleanAlias = PatternOr([PatternAs(PatternBool(true))("value"), PatternAs(PatternBool(false))("value")])
                                                                                                                                            in
                                                                                                                                                let orPattern = ExprMatch(ExprBool(true))([(booleanAlias, ExprVar("value"), None)])(None)
                                                                                                                                                in
                                                                                                                                                    let orPatternChecked = expectProgramType("Bool")(ProgramSyntax(items = [], body = Some(orPattern)))
                                                                                                                                                    in
                                                                                                                                                        let wrongArityMatch = ExprMatch(ExprCall(ExprVar("Some"))(ExprInt(1))(false))([(PatternConstructor("Some")([]), ExprInt(0), None)])(None)
                                                                                                                                                        in
                                                                                                                                                            let wrongArityChecked =
                                                                                                                                                                match inferProgram(ProgramSyntax(items = [TopLevelType(optionType)], body = Some(wrongArityMatch))) with
                                                                                                                                                                    | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(ConstructorPatternArityMismatch("Some"))) } -> Unit
                                                                                                                                                                    | _ -> test.fail("constructor pattern arity should be checked")
                                                                                                                                                            in
                                                                                                                                                                let unknownFieldMatch = ExprMatch(pointValue)([(PatternRecord("Point")([("missing", PatternWildcard)]), ExprInt(0), None)])(None)
                                                                                                                                                                in
                                                                                                                                                                    let unknownFieldChecked =
                                                                                                                                                                        match inferProgram(ProgramSyntax(items = [TopLevelType(pointType)], body = Some(unknownFieldMatch))) with
                                                                                                                                                                            | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(UnknownRecordPatternField("Point", "missing"))) } -> Unit
                                                                                                                                                                            | _ -> test.fail("record pattern fields should be checked")
                                                                                                                                                                    in
                                                                                                                                                                        let inconsistentPattern = PatternOr([PatternVar("value"), PatternWildcard])
                                                                                                                                                                        in
                                                                                                                                                                            let inconsistentMatch = ExprMatch(ExprBool(true))([(inconsistentPattern, ExprBool(true), None)])(None)
                                                                                                                                                                            in
                                                                                                                                                                                let inconsistentChecked =
                                                                                                                                                                                    match inferProgram(ProgramSyntax(items = [], body = Some(inconsistentMatch))) with
                                                                                                                                                                                        | ProgramInferenceResult { semanticType = _semanticType, substitution = _substitution, environment = _environment, error = Some(ProgramExpressionError(InconsistentOrPatternBindings)) } -> Unit
                                                                                                                                                                                        | _ -> test.fail("or-pattern alternatives should bind the same names")
                                                                                                                                                                                in Ashes.IO.print("all self-hosted type declaration inference tests passed"))
