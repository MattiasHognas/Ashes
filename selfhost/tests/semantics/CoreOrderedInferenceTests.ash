import AshesCompiler.Frontend.Parser
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.IrText
export (
    value runCoreOrderedInferenceTests,
)

let lowerSource source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> lowerCoreProgram(program)
        | _ -> Ashes.IO.panic("ordered inference regression did not parse")

let expectBool source =
    match lowerSource(source) with
        | CoreLoweringResult { program = Some(program), semanticType = SemBool, error = None } -> program
        | CoreLoweringResult { error = Some(error) } -> Ashes.IO.panic("ordered inference failed: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("ordered inference did not produce Bool")

let standardTraits unit =
    match Ashes.IO.File.readText("lib/Ashes/Trait.ash") with
        | Ok(source) -> source
        | Error(message) -> Ashes.IO.panic(message)

let checkStringOperator traits operator =
    (let program = expectBool(traits + "\nlet before left right = left " + operator + " right\nbefore(\"a\")(\"z\")")
    in
        if Ashes.Text.contains(None
        |> formatIr(program)(LoweredIr)
        |> Ashes.Text.join("\n"))("GetAdtTag")
        then Unit
        else Ashes.IO.panic("ordered string replay did not emit Ord dispatch"))

let checkPrimitive source instruction =
    if Ashes.Text.contains(None
    |> formatIr(expectBool(source))(LoweredIr)
    |> Ashes.Text.join("\n"))(instruction)
    then Unit
    else Ashes.IO.panic("ordered replay lost primitive comparison: " + instruction)

let checkOneKnownOperand traits unit =
    match expectBool(traits + "\nlet before left = left <= \"z\"\nbefore(\"a\")") with
        | _ ->
            match expectBool(traits + "\nlet before right = \"a\" <= right\nbefore(\"z\")") with
                | _ -> Unit

let checkActiveEvidence traits unit =
    match expectBool(traits + "\ntype Box(a) = | Box(a)\nimplement Eq(Box(a)) requires {Ord(a)} = | equal = given left -> given right -> match (left, right) with | (Box(x), Box(y)) -> x <= y\nlet same (left: Box(Str)) (right: Box(Str)) = left == right\nsame(Box(\"a\"))(Box(\"z\"))") with
        | _ -> Unit

let checkCustomEvidence traits unit =
    match expectBool(traits + "\ntype Rank = | Rank(Int) deriving {Eq}\nimplement Ord(Rank) = | compare = given left -> given right -> match (left, right) with | (Rank(x), Rank(y)) -> if x < y then Less else if x == y then Equal else Greater\nlet before left right = left <= right\nbefore(Rank(1))(Rank(2))") with
        | _ -> Unit

let checkSuppliedPredicateEvidence traits unit =
    match expectBool(traits + "\ntype Rank = | Rank(Int) deriving {Eq}\nimplement Ord(Rank) = | compare = given left -> given right -> Less | lessOrEqual = given left -> given right -> false\ntype Box(a) = | Box(a)\nimplement Eq(Box(a)) requires {Ord(a)} = | equal = given left -> given right -> match (left, right) with | (Box(x), Box(y)) -> x <= y\nlet same (left: Box(Rank)) (right: Box(Rank)) = left == right\nsame(Box(Rank(1)))(Box(Rank(2)))") with
        | _ -> Unit

let checkGenericHeadEvidence traits unit =
    match expectBool(traits + "\ntype Ranked(a) = | Ranked(a) deriving {Eq}\nimplement Ord(Ranked(a)) requires {Ord(a)} = | compare = given left -> given right -> match (left, right) with | (Ranked(x), Ranked(y)) -> if x < y then Less else if x > y then Greater else Equal\ntype Box(a) = | Box(a)\nimplement Eq(Box(a)) requires {Ord(a)} = | equal = given left -> given right -> match (left, right) with | (Box(x), Box(y)) -> x <= y\nlet same (left: Box(Ranked(Str))) (right: Box(Ranked(Str))) = left == right\nsame(Box(Ranked(\"a\")))(Box(Ranked(\"z\")))") with
        | _ -> Unit

let checkMismatchedOperands unit =
    match lowerSource("1 <= \"z\"") with
        | CoreLoweringResult { error = Some(CoreOperatorTypeMismatch("<=", SemInt, SemString)) } -> Unit
        | _ -> Ashes.IO.panic("ordered comparison lost operand mismatch")

let checkMissingEvidence unit =
    match lowerSource("type Plain = | Plain\nlet before left right = left <= right\nbefore(Plain)(Plain)") with
        | CoreLoweringResult { error = Some(MissingCoreTraitEvidence("Ord", SemNamed(_id, "Plain", []))) } -> Unit
        | CoreLoweringResult { error = Some(error) } -> Ashes.IO.panic("unexpected missing Ord diagnostic: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("ordered comparison accepted missing evidence")

// Stage 1 does not yet generalize constrained bindings at multiple concrete types.
// Preserve that documented boundary instead of silently choosing one comparison.
let checkMixedUses traits unit =
    match lowerSource(traits + "\nlet before left right = left <= right\n(before(1)(2), before(\"a\")(\"z\"))") with
        | CoreLoweringResult { error = Some(CoreCallTypeMismatch(_mismatch, _site)) } -> Unit
        | CoreLoweringResult { error = Some(error) } -> Ashes.IO.panic("mixed Ord lost its type mismatch: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("mixed constrained uses must fail closed")

// Use the actual library declarations, including the unannotated comparator that
// blocked the stage-2 bootstrap, rather than a reduced copy of its signature.
let checkStandardListSort traits unit =
    match Ashes.IO.File.readText("lib/Ashes/Collection.List.ash") with
        | Error(message) -> Ashes.IO.panic(message)
        | Ok(source) ->
            match expectBool(traits + "\n" + source + "\nmatch sort([\"z\", \"a\"]) with | first :: second :: [] -> first == \"a\" && second == \"z\" | _ -> false") with
                | _ -> Unit

let checkExpressionBoundary unit =
    match parseExpression("given left -> given right -> left <= right") with
        | ExpressionParseResult { expression = expression, diagnostics = [] } ->
            match lowerCoreExpression(expression) with
                | CoreLoweringResult { error = Some(MissingCoreTraitEvidence("Ord", SemVariable(_id))) } -> Unit
                | _ -> Ashes.IO.panic("non-replaying ordered expression emitted an unresolved goal")
        | _ -> Ashes.IO.panic("ordered expression regression did not parse")

let runCoreOrderedInferenceTests unit =
    (let traits = standardTraits(Unit)
    in
        Unit
        |> (given (_) -> checkStringOperator(traits)("<"))
        |> (given (_) -> checkStringOperator(traits)("<="))
        |> (given (_) -> checkStringOperator(traits)(">"))
        |> (given (_) -> checkStringOperator(traits)(">="))
        |> (given (_) -> checkPrimitive("let before left right = left < right\nbefore(1)(2)")("CmpIntLt"))
        |> (given (_) -> checkPrimitive("let before left right = left >= right\nbefore(2.0)(1.0)")("CmpFloatGe"))
        |> checkOneKnownOperand(traits)
        |> checkActiveEvidence(traits)
        |> checkCustomEvidence(traits)
        |> checkSuppliedPredicateEvidence(traits)
        |> checkGenericHeadEvidence(traits)
        |> checkMismatchedOperands
        |> checkMissingEvidence
        |> checkMixedUses(traits)
        |> checkExpressionBoundary
        |> checkStandardListSort(traits))
