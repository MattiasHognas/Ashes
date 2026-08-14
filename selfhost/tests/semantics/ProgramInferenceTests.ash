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
                    let optionType = TypeDecl(name = "Option", typeParameters = [TypeParameter(name = "a")], constructors = [TypeConstructor(name = "None", parameters = [], fieldNames = []), TypeConstructor(name = "Some", parameters = [TypeNamed("a")], fieldNames = [])], isRecord = false, derivingTraits = [])
                    in
                        let constructorProgram = ProgramSyntax(items = [TopLevelType(optionType)], body = Some(ExprCall(ExprVar("Some"))(ExprString("value"))(false)))
                        in
                            let constructorChecked = expectProgramType("Option(Str)")(constructorProgram)
                            in Ashes.IO.print("all self-hosted program inference tests passed"))
