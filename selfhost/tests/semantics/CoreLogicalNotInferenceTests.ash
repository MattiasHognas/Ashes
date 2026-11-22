import CoreOrderedInferenceTests.standardTraits
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Frontend.Parser
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.IrText
export (
    value runCoreLogicalNotInferenceTests,
)

let lowerSource source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> lowerCoreProgram(program)
        | _ -> Ashes.IO.panic("logical-not regression did not parse")

let expectBool source =
    match lowerSource(source) with
        | CoreLoweringResult { semanticType = SemBool, error = None } -> Unit
        | CoreLoweringResult { error = Some(error) } -> Ashes.IO.panic("logical-not lowering failed: " + Ashes.Trait.Show.show(error) + "\n" + source)
        | _ -> Ashes.IO.panic("logical-not did not infer Bool")

let checkUnresolvedOperands unit =
    Unit
    |> (given (_) -> expectBool("let invert value = !value\ninvert(false)"))
    |> (given (_) -> expectBool("let identity value = value\nlet invert value = !identity(value)\ninvert(false)"))
    |> (given (_) -> expectBool("let invert : Bool -> Bool = given value -> !value\ninvert(false)"))
    |> (given (_) -> expectBool("let recursive even n = if n == 0 then true else !even(n - 1)\neven(10)"))
    |> (given (_) -> expectBool("let recursive first n = if n == 0 then true else !second(n - 1) and second n = if n == 0 then true else !first(n - 1)\nfirst(10)"))
    |> (given (_) -> expectBool(standardTraits(Unit) + "\nlet recursive retained values rejected = match values with | [] -> [] | value :: rest -> if !contains(value)(rejected) then value :: retained(rest)(rejected) else retained(rest)(rejected) and contains value values = match values with | [] -> false | candidate :: rest -> candidate == value || contains(value)(rest)\nmatch retained([1, 2])([2]) with | value :: [] -> value == 1 | _ -> false"))

let checkInvalidOperand unit =
    match lowerSource("!1") with
        | CoreLoweringResult { error = Some(CoreOperatorTypeMismatch("not", SemInt, SemInt)) } -> Unit
        | _ -> Ashes.IO.panic("logical-not changed the invalid operand diagnostic")

// An earlier deferred goal forces whole-program replay. The recursive group's
// provisional member pass must still settle its later sibling before final emission.
let checkCombinedReplay unit = expectBool("let invert value = !value\nlet recursive even n = if n == 0 then true else !odd(n - 1) and odd n = if n == 0 then true else !even(n - 1)\ninvert(false) && even(10)")

// A recursive group's final member pass during a pinned whole-program replay first lowers a body
// with its parameter still open. An equality over that parameter, closed later in the same body,
// is recorded rather than rejected, and the body's lowering again answers it.
let checkRecursiveGroupEqualityReplay unit = expectBool(standardTraits(Unit) + "\nlet recursive hide names name = match names with | [] -> [] | other :: rest -> if other != name then other :: hide(rest)(name) else hide(rest)(name)\nlet recursive count names values = if names == [] then 0 else match values with | [] -> 0 | _value :: rest -> step(count(names))(rest) + count(hide(names)(\"x\"))([]) and step recur rest = 1 + recur(rest)\nlet ordered = ((given (left) -> given (right) -> left >= right))(\"b\")(\"a\")\nordered && count([\"x\", \"y\"])([5, 6, 7]) == 3")

let checkActiveGenericEvidence unit = expectBool("type Bit = | Bit(Bool) deriving {Eq}\nimplement Not(Bit) = | not = given bit -> match bit with | Bit(value) -> Bit(!value)\ntype Box(a) = | Box(a)\nimplement Eq(Box(a)) requires {Not(a), Eq(a)} = | equal = given left -> given right -> match (left, right) with | (Box(x), Box(y)) -> !x == y\nlet same (left: Box(Bit)) (right: Box(Bit)) = left == right\nsame(Box(Bit(false)))(Box(Bit(true)))")

let bitNotPrelude = "type Bit = | Bit(Bool)\nimplement Not(Bit) = | not = given bit -> match bit with | Bit(value) -> Bit(!value)\n"

// An ordinary binding with an explicit or an inferred Not requirement takes its evidence per
// call, so a custom operand keeps its own type instead of the binding settling to Bool -> Bool.
let checkGenericNotEvidence signature =
    match lowerSource(bitNotPrelude + "let invert" + signature + " = given value -> !value\ninvert(Bit(false))") with
        | CoreLoweringResult { program = Some(_program), semanticType = SemNamed(_id, "Bit", []), error = None } -> Unit
        | CoreLoweringResult { error = Some(error) } -> Ashes.IO.panic("generic Not did not lower: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("generic Not must keep its operand type instead of specializing to Bool")

// One constrained binding serves a Bool and a custom operand in the same program.
let checkMixedNotEvidence unit =
    match lowerSource(bitNotPrelude + "let invert : a -> a requires {Not(a)} = given value -> !value\n(invert(false), invert(Bit(false)))") with
        | CoreLoweringResult { program = Some(_program), semanticType = SemTuple(SemBool :: SemNamed(_id, "Bit", []) :: []), error = None } -> Unit
        | CoreLoweringResult { error = Some(error) } -> Ashes.IO.panic("mixed generic Not did not lower: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("mixed generic Not uses must each keep their own operand type")

// Each lambda is lowered before the argument that settles its goal. Share its
// unnumbered source span across calls, as can happen between stitched modules.
let operatorApplication body argument =
    ExprCall(ExprLambda("x")(ExprAt(TextSpan(start = 0, end = 1))(body))(None))(argument)(false)(callArgumentsInline)

let coincidentOperatorCalls unit =
    (let x = ExprVar("x")
    in
        ExprTuple([
            operatorApplication(ExprLogicalNot(x))(ExprBool(false)),
            callArgumentsInline
            |> ExprCall(ExprVar("Bit"))(ExprBool(false))(false)
            |> operatorApplication(ExprLogicalNot(x)),
            operatorApplication(ExprLessThan(x)(x))(ExprInt(1)),
            "1.5"
            |> ExprFloat(1.5)
            |> operatorApplication(ExprLessThan(x)(x))
        ]))

let checkCoincidentOperatorIr program =
    match lowerCoreProgram(program) with
        | CoreLoweringResult { program = Some(ir), semanticType = SemTuple(SemBool :: SemNamed(_id, "Bit", []) :: SemBool :: SemBool :: []), error = None } ->
            let text =
                None
                |> formatIr(ir)(LoweredIr)
                |> Ashes.Text.join("\n")
            in
                if Ashes.Text.contains(text)("CmpIntLt") && Ashes.Text.contains(text)("CmpFloatLt")
                then Unit
                else Ashes.IO.panic("coincident comparisons lost an operand type")
        | _ -> Ashes.IO.panic("coincident operator sites lost their result types")

let checkCoincidentOperatorSpans unit =
    match parseProgram(bitNotPrelude) with
        | ProgramParseResult { program = declarations, diagnostics = [] } ->
            let program =
                declarations with body = Some(coincidentOperatorCalls(Unit))
            in
                match measureCoreProgramLowering(given (_) -> 0)(None)("colliding.ash")(bitNotPrelude)(program) with
                    | CoreLoweringMeasurement { pendingGoalKeys = keys, replayed = true, replay = CorePassCounts { error = None } } ->
                        let count =
                            given (key) ->
                                keys
                                |> Ashes.Collection.List.filter(given (entry) -> entry == key)
                                |> Ashes.Collection.List.length
                        in
                            if count("0:1:Not:not") == 2 && count("0:1:Ord:less") == 2
                            then checkCoincidentOperatorIr(program)
                            else Ashes.IO.panic("operator fixture did not defer both visits: " + Ashes.Trait.Show.show(keys))
                    | CoreLoweringMeasurement { replay = CorePassCounts { error = error } } -> Ashes.IO.panic("coincident operator sites did not replay: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("Not instance did not parse")

let lowerExpressionSource source =
    match parseExpression(source) with
        | ExpressionParseResult { expression = expression, diagnostics = [] } -> lowerCoreExpression(expression)
        | _ -> Ashes.IO.panic("expression regression did not parse")

// The expression entry point lowers once, without replay and without the standard trait module.
// A constrained binding called at Bool asks for the Not(Bool) dictionary, whose method lives in
// that module, so the call is rejected rather than lowered with a placeholder.
let checkExpressionBoundary unit =
    match lowerExpressionSource("let invert x = !x in invert(false)") with
        | CoreLoweringResult { error = Some(UnknownLoweringBinding("__ashes_standard_trait_Not_not_bool")) } ->
            match lowerExpressionSource("!false") with
                | CoreLoweringResult { program = Some(program), semanticType = SemBool, error = None } ->
                    if Ashes.Text.contains(None
                    |> formatIr(program)(LoweredIr)
                    |> Ashes.Text.join("\n"))("CmpIntEq")
                    then Unit
                    else Ashes.IO.panic("expression boundary emitted a placeholder")
                | _ -> Ashes.IO.panic("concrete expression negation failed")
        | CoreLoweringResult { error = Some(error) } -> Ashes.IO.panic("non-replaying expression changed its unresolved-goal diagnostic: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("non-replaying expression accepted an unresolved goal")

let checkShowExpressionBoundary unit =
    match lowerExpressionSource("given value -> Ashes.Trait.Show.show(value)") with
        | CoreLoweringResult { error = Some(MissingCoreTraitEvidence("Show", SemVariable(_id))) } -> Unit
        | _ -> Ashes.IO.panic("non-replaying Show expression accepted an unresolved goal")

let checkEscapingDictionary unit =
    match lowerExpressionSource("let invert x = !x in invert") with
        | CoreLoweringResult { program = None, error = Some(MissingCoreTraitEvidence("Not", SemVariable(_id))) } -> Unit
        | _ -> Ashes.IO.panic("unresolved dictionary escaped the expression boundary")

let checkRecursiveExpressionBoundary unit =
    match lowerExpressionSource("let recursive even n = if n == 0 then true else !even(n - 1) in even(10)") with
        | CoreLoweringResult { program = Some(program), semanticType = SemBool, error = None } ->
            if Ashes.Text.contains(None
            |> formatIr(program)(LoweredIr)
            |> Ashes.Text.join("\n"))("CmpIntEq")
            then
                match lowerExpressionSource("let recursive invert x = !x in invert") with
                    | CoreLoweringResult { program = None, error = Some(MissingCoreTraitEvidence("Not", SemVariable(_id))) } -> Unit
                    | CoreLoweringResult { error = Some(error) } -> Ashes.IO.panic("escaping recursive Not changed its diagnostic: " + Ashes.Trait.Show.show(error))
                    | _ -> Ashes.IO.panic("recursive discovery placeholder escaped")
            else Ashes.IO.panic("recursive expression did not emit negation")
        | _ -> Ashes.IO.panic("recursive expression inference failed")

let runCoreLogicalNotInferenceTests unit =
    Unit
    |> checkUnresolvedOperands
    |> checkInvalidOperand
    |> checkCombinedReplay
    |> checkRecursiveGroupEqualityReplay
    |> checkActiveGenericEvidence
    |> (given (_) -> checkGenericNotEvidence(" : a -> a requires {Not(a)}"))
    |> (given (_) -> checkGenericNotEvidence(""))
    |> checkMixedNotEvidence
    |> checkCoincidentOperatorSpans
    |> checkExpressionBoundary
    |> checkShowExpressionBoundary
    |> checkEscapingDictionary
    |> checkRecursiveExpressionBoundary
