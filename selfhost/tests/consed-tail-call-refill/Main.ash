import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.TcoRuntimeManagedParams
let call name argument = ExprCall(ExprVar(name))(argument)(false)(callArgumentsInline)

let pop = call("walk")(ExprVar("rest"))

let refill =
    ExprVar("rest")
    |> call("refill")
    |> call("walk")

let branch first second third =
    third
    |> ExprIf(ExprVar("d"))(second)
    |> ExprIf(ExprVar("c"))(first)

let underPending body = ExprMatch(ExprVar("pending"))([(PatternEmptyList, ExprVar("Unit"), None), (PatternCons(PatternVar("head"))(PatternVar("rest")), body, None)])(None)

let check expected body =
    tcoSelfCallShapes("walk")(["pending"])(underPending(body)) == [expected]

let checks unit =
    (let push =
        ExprVar("rest")
        |> ExprCons(ExprVar("fresh"))
        |> call("walk")
    in
        let unsafePush =
            ExprVar("rest")
            |> ExprCons(ExprVar("pending"))
            |> call("walk")
        in
            let wrongTail =
                ExprVar("other")
                |> ExprCons(ExprVar("fresh"))
                |> call("walk")
            in
                let boundRefill =
                    ExprLet("next")(call("refill")(ExprVar("rest")))(call("walk")(ExprVar("next")))([])(None)([])
                in
                    check(TcoChoiceAccumulatorShape)(branch(pop)(push)(refill)) && check(TcoChoiceAccumulatorShape)(branch(pop)(push)(boundRefill)) && check(TcoWalkedOrRefilledShape)(ExprIf(ExprVar("c"))(pop)(refill)) && check(TcoOtherShape)(branch(pop)(unsafePush)(refill)) && check(TcoOtherShape)(branch(pop)(wrongTail)(refill)) && check(TcoOtherShape)(ExprVar("unknown")
                    |> call("walk")
                    |> branch(pop)(push)))

if checks(Unit)
then Ashes.IO.print("consed tail call refill shapes ok")
else Ashes.IO.panic("consed tail call refill shape mismatch")
