import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.Unification
let call name argument = ExprCall(ExprVar(name))(argument)(false)(callArgumentsInline)

let value captures constrained =
    (let result =
        if captures
        then call("f")(ExprVar("x"))
        else ExprVar("x")
    in
        ExprLambda("x")(if constrained
        then
            ExprIf(ExprEqual(ExprVar("x"))(ExprVar("x")))(result)(result)
        else result)(None))

let uses name mixed =
    ExprTuple([call(name)(ExprInt(1)), call(name)(if mixed
    then ExprString("text")
    else ExprInt(2))])

let expectInference name accepted result =
    match result with
        | TypeInferenceResult { error = None } ->
            if accepted
            then Unit
            else test.fail(name + ": accepted captured Int/Str calls")
        | TypeInferenceResult { error = Some(InferenceUnificationError(TypeMismatch(SemInt, SemString))) } ->
            if accepted
            then test.fail(name + ": rejected valid polymorphism")
            else Unit
        | TypeInferenceResult { error = Some(error) } -> test.fail(name + ": unexpected error " + Ashes.Trait.Show.show(error))

let environment unit =
    Unit
    |> emptyTypeEnvironment
    |> addTypeBinding("f")(TypeScheme(quantified = [], body = SemVariable(10), constraints = []))

let declaration name expression = LetBindingSyntax(name = name, value = expression, sugarParameters = [], typeAnnotation = None, requirements = [], declarationStart = -1)

let checkCase captures constrained mixed =
    (let accepted =
        if captures
        then
            if mixed
            then false
            else true
        else true
    in
        let body = value(captures)(constrained)
        in
            let calls = uses("apply")(mixed)
            in
                Unit
                |> (given (_) ->
                    Unit
                    |> emptyTypeEnvironment
                    |> inferExpression(ExprLambda("f")(ExprLet("apply")(body)(calls)([])(None)([]))(None))
                    |> expectInference("let")(accepted))
                |> (given (_) ->
                    Unit
                    |> emptyTypeEnvironment
                    |> inferExpression(ExprLambda("f")(ExprLetRecursive("apply")(body)(calls)([])(None)([]))(None))
                    |> expectInference("let-rec")(accepted))
                |> (given (_) ->
                    match 2000
                    |> typeVariableSupplyFrom
                    |> inferTopLevelBinding("apply")(body)(None)([])(environment(Unit))([]) with
                        | TopLevelBindingInferenceResult { environment = nextEnvironment, substitution = substitution, supply = supply, error = None } ->
                            supply
                            |> inferExpressionFrom(calls)(nextEnvironment)(substitution)
                            |> expectInference("top-level")(accepted)
                        | TopLevelBindingInferenceResult { error = Some(error) } -> test.fail("binding unexpectedly failed: " + Ashes.Trait.Show.show(error)))
                |> (given (_) ->
                    let program =
                        ProgramSyntax(items = [TopLevelRecursiveGroup([declaration("apply")(body), None
                        |> ExprLambda("x")(call("apply")(ExprVar("x")))
                        |> declaration("forward")])], body = mixed
                        |> uses("forward")
                        |> Some)
                    in
                        match inferProgramFromPackage("standalone")(environment(Unit))(program) with
                            | ProgramInferenceResult { error = None } ->
                                if accepted
                                then Unit
                                else test.fail("recursive group accepted captured Int/Str calls")
                            | ProgramInferenceResult { error = Some(ProgramExpressionError(InferenceUnificationError(TypeMismatch(SemInt, SemString)))) } ->
                                if accepted
                                then test.fail("recursive group rejected valid polymorphism")
                                else Unit
                            | ProgramInferenceResult { error = Some(error) } -> test.fail("recursive group unexpected error: " + Ashes.Trait.Show.show(error))))

let checkScheme unit =
    (let outer = TypeScheme(quantified = [(40, "a")], body = SemFunction(SemVariable(40))(SemVariable(10))(Some(SemVariable(12))), constraints = [TraitConstraint(traitName = "Eq", typeArguments = [SemVariable(11)])])
    in
        let substitution = [(10, SemVariable(20)), (20, SemTuple([SemVariable(21), SemVariable(22)])), (11, SemVariable(23)), (12, SemVariable(24)), (40, SemVariable(30))]
        in
            let candidate = SemTuple([SemVariable(21), SemVariable(22), SemVariable(23), SemVariable(24), SemVariable(30)])
            in
                match generalizeUnderSubstitution([outer])(substitution)(candidate)([]) with
                    | TypeScheme { quantified = (30, _name) :: [] } -> Unit
                    | _ -> test.fail("generalization must resolve free outer variables, constraints and rows, excluding quantified variables first"))

let runCapturedEnvironmentInferenceTests unit =
    Unit
    |> checkScheme
    |> (given (_) -> checkCase(true)(false)(true))
    |> (given (_) -> checkCase(true)(true)(true))
    |> (given (_) -> checkCase(true)(false)(false))
    |> (given (_) -> checkCase(true)(true)(false))
    |> (given (_) -> checkCase(false)(false)(true))
    |> (given (_) -> checkCase(false)(true)(true))
