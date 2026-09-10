import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.RecursiveProducerResult
export (
    value runRecursiveProducerResultTests,
)

let resolve name =
    if name == "produce"
    then RecursiveProducerFunction
    else
        if name == "result"
        then RecursiveProducerValue
        else OtherProducerBinding

let recursiveCall = ExprCall(ExprVar("produce"))(ExprInt(0))(false)(callArgumentsInline)

let expectLexicalProvenance unit =
    Unit
    |> (given (_) ->
        resolve
        |> isRecursiveProducerResult(recursiveCall)
        |> test.assertEqual(true))
    |> (given (_) ->
        resolve
        |> isRecursiveProducerResult(ExprLet("alias")(ExprVar("result"))(ExprVar("alias"))([])(None)([]))
        |> test.assertEqual(true))
    |> (given (_) ->
        resolve
        |> isRecursiveProducerResult(ExprLet("result")(ExprList([])(false))(ExprVar("result"))([])(None)([]))
        |> test.assertEqual(false))
    |> (given (_) ->
        resolve
        |> isRecursiveProducerResult(ExprLet("produce")(ExprInt(0))(recursiveCall)([])(None)([]))
        |> test.assertEqual(false))
    |> (given (_) ->
        resolve
        |> isRecursiveProducerResult(ExprIf(ExprBool(true))(recursiveCall)(ExprVar("result")))
        |> test.assertEqual(true))
    |> (given (_) ->
        resolve
        |> isRecursiveProducerResult(false
        |> ExprList([])
        |> ExprIf(ExprBool(true))(recursiveCall))
        |> test.assertEqual(false))
    |> (given (_) ->
        resolve
        |> isRecursiveProducerResult(ExprMatch(ExprInt(1))([(PatternVar("result"), ExprVar("result"), None)])(None))
        |> test.assertEqual(false))

let recursive countOwnedCells instructions =
    match instructions with
        | [] -> 0
        | IrInstruction { instruction = Alloc(_target, 16, true) } :: rest -> 1 + countOwnedCells(rest)
        | _ :: rest -> countOwnedCells(rest)

let recursive countFunctionCells (functions: List(IrFunction)) =
    match functions with
        | [] -> 0
        | function :: rest -> countOwnedCells(function.instructions) + countFunctionCells(rest)

let recursive expectProducerInstructions label instructions =
    match instructions with
        | [] -> Unit
        | IrInstruction { instruction = Alloc(_target, 16, false) } :: _ -> test.fail("recursive producer spine must be owned")
        | IrInstruction { instruction = MakeClosure(_target, callee, _environment, _size, _managed, returns, _accepts) } :: rest ->
            if callee == label && returns == false
            then test.fail("self closure must report its runtime-managed result")
            else expectProducerInstructions(label)(rest)
        | _ :: rest -> expectProducerInstructions(label)(rest)

let recursive expectProducerFunctions (functions: List(IrFunction)) =
    match functions with
        | [] -> Unit
        | function :: rest ->
            function.instructions
            |> expectProducerInstructions(function.label)
            |> (given (_) -> expectProducerFunctions(rest))

let expectRecursiveTailIsOwned body =
    (let source = "let recursive produce (count: Int) = if count == 0 then [] else " + body + "\nproduce(3)"
    in
        match parseProgram(source) with
            | ProgramParseResult { program = syntax, diagnostics = [] } ->
                match lowerCoreProgram(syntax) with
                    | CoreLoweringResult { program = Some(program), error = None } ->
                        countFunctionCells(program.functions) > 0
                        |> test.assertEqual(true)
                        |> (given (_) -> expectProducerFunctions(program.functions))
                    | _ -> test.fail("recursive producer should lower")
            | _ -> test.fail("recursive producer should parse"))

let runRecursiveProducerResultTests unit =
    Unit
    |> expectLexicalProvenance
    |> (given (_) -> expectRecursiveTailIsOwned("7 :: produce(count - 1)"))
    |> (given (_) -> expectRecursiveTailIsOwned("let result = produce(count - 1) in let alias = result in 7 :: alias"))
    |> (given (_) -> expectRecursiveTailIsOwned("let result = if count > 2 then produce(count - 1) else produce(0) in 7 :: result"))
    |> (given (_) -> expectRecursiveTailIsOwned("let result = match count with | 1 -> produce(0) | _ -> produce(count - 1) in 7 :: result"))
