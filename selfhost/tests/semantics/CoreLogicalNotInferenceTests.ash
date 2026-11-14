import AshesCompiler.Frontend.Parser
import AshesCompiler.Semantics.CoreLowering
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
        | CoreLoweringResult { error = Some(error) } -> Ashes.IO.panic("logical-not lowering failed: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("logical-not did not infer Bool")

let checkUnresolvedOperands unit =
    Unit
    |> (given (_) -> expectBool("let invert value = !value\ninvert(false)"))
    |> (given (_) -> expectBool("let identity value = value\nlet invert value = !identity(value)\ninvert(false)"))
    |> (given (_) -> expectBool("let invert : Bool -> Bool = given value -> !value\ninvert(false)"))
    |> (given (_) -> expectBool("let recursive even n = if n == 0 then true else !even(n - 1)\neven(10)"))
    |> (given (_) -> expectBool("let recursive first n = if n == 0 then true else !second(n - 1) and second n = if n == 0 then true else !first(n - 1)\nfirst(10)"))
    |> (given (_) -> expectBool("let recursive retained values rejected = match values with | [] -> [] | value :: rest -> if !contains(value)(rejected) then value :: retained(rest)(rejected) else retained(rest)(rejected) and contains value values = match values with | [] -> false | candidate :: rest -> candidate == value || contains(value)(rest)\nmatch retained([1, 2])([2]) with | value :: [] -> value == 1 | _ -> false"))

let checkInvalidOperand unit =
    match lowerSource("!1") with
        | CoreLoweringResult { error = Some(CoreOperatorTypeMismatch("not", SemInt, SemInt)) } -> Unit
        | _ -> Ashes.IO.panic("logical-not changed the invalid operand diagnostic")

// An earlier deferred goal forces whole-program replay. The recursive group's
// provisional member pass must still settle its later sibling before final emission.
let checkCombinedReplay unit = expectBool("let invert value = !value\nlet recursive even n = if n == 0 then true else !odd(n - 1) and odd n = if n == 0 then true else !even(n - 1)\ninvert(false) && even(10)")

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

let checkRecursiveExpressionBoundary unit =
    match lowerExpressionSource("let recursive even n = if n == 0 then true else !even(n - 1) in even(10)") with
        | CoreLoweringResult { program = Some(program), semanticType = SemBool, error = None } ->
            if Ashes.Text.contains(None
            |> formatIr(program)(LoweredIr)
            |> Ashes.Text.join("\n"))("CmpIntEq")
            then
                match lowerExpressionSource("let recursive invert x = !x in invert") with
                    | CoreLoweringResult { error = Some(MissingCoreTraitEvidence("Not", SemVariable(_id))) } -> Unit
                    | CoreLoweringResult { error = Some(error) } -> Ashes.IO.panic("escaping recursive Not changed its diagnostic: " + Ashes.Trait.Show.show(error))
                    | _ -> Ashes.IO.panic("recursive discovery placeholder escaped")
            else Ashes.IO.panic("recursive expression did not emit negation")
        | _ -> Ashes.IO.panic("recursive expression inference failed")

let runCoreLogicalNotInferenceTests unit =
    Unit
    |> checkUnresolvedOperands
    |> checkInvalidOperand
    |> checkCombinedReplay
    |> checkActiveGenericEvidence
    |> (given (_) -> checkGenericNotEvidence(" : a -> a requires {Not(a)}"))
    |> (given (_) -> checkGenericNotEvidence(""))
    |> checkMixedNotEvidence
    |> checkExpressionBoundary
    |> checkShowExpressionBoundary
    |> checkRecursiveExpressionBoundary
