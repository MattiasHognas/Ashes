// The registration and naming half of stage 0's `f$reuse` whole-function reuse specialization
// (`Lowering.TopLevel.cs`'s `RegisterRecursiveReuseCandidate`, `Lowering.Reuse.cs`'s
// `GetOrCreateReuseSpecialization` label scheme). A self-recursive top-level function whose LAST
// parameter is the accumulator it rewrites is a specialization candidate: called with a provably
// unique argument, the same body lowered with that parameter treated as a linear reuse root
// rebuilds the accumulator in place. Everything here is a structural scan over the parsed program;
// the call-site proofs that need types, and the generation itself, stay in CoreLowering.ash, and
// the reset-safety verdict on the generated body is `ReuseResetSafety.ash`.
//
// Invariants:
// - Only a top-level binding is a candidate. A nested `let recursive` is skipped, so a
//   specialization's free references are the globally resolvable ones a generated body can bind.
// - A candidate's registered value is its whole curried lambda chain, so generation lowers exactly
//   the function that was written, not a reconstruction of it.
// - The first specialization of a name takes the bare `name__reuse` label and later
//   instantiations are suffixed, stage 0's naming, so a single-instantiation program's IR carries
//   stage 0's own label.

import Ashes.Collection.List.append
import AshesCompiler.Frontend.Syntax
export (
    value reuseSpecializationCandidates,
    value reuseSpecializationLabel,
    value collectSpecializableCallArgs,
)

let recursive specializationLambdaChain (value: Expr) (reversedParameters: List(Str)) =
    match value with
        | ExprAt(_span, inner) -> specializationLambdaChain(inner)(reversedParameters)
        | ExprLambda(parameter, body, _annotation) -> specializationLambdaChain(body)(parameter :: reversedParameters)
        | _ -> reversedParameters

let recursive reverseNames (names: List(Str)) (reversed: List(Str)) =
    match names with
        | [] -> reversed
        | name :: rest -> reverseNames(rest)(name :: reversed)

// One candidate: the binding's name, its curried parameter names outermost first, and its value.
// A binding whose value is not a lambda contributes nothing.
let specializationCandidateOf (name: Str) (value: Expr) =
    match specializationLambdaChain(value)([]) with
        | [] -> None
        | reversedParameters -> Some((name, reverseNames(reversedParameters)([]), value))

let recursive consCandidate (candidate: Maybe((Str, List(Str), Expr))) (rest: List((Str, List(Str), Expr))) =
    match candidate with
        | Some(found) -> found :: rest
        | None -> rest

// Every self-recursive top-level function of the program, in declaration order. A recursive group
// of more than one binding is skipped: mutual recursion has no single accumulator to make linear.
let recursive reuseSpecializationCandidates (items: List(TopLevelItem)) =
    match items with
        | [] -> []
        | TopLevelAt(_span, inner) :: rest -> reuseSpecializationCandidates(inner :: rest)
        | TopLevelLet(LetBindingSyntax { name = name, value = value }, true) :: rest ->
            rest
            |> reuseSpecializationCandidates
            |> consCandidate(specializationCandidateOf(name)(value))
        | TopLevelRecursiveGroup(LetBindingSyntax { name = name, value = value } :: []) :: rest ->
            rest
            |> reuseSpecializationCandidates
            |> consCandidate(specializationCandidateOf(name)(value))
        | _ :: rest -> reuseSpecializationCandidates(rest)

// The label of the `index`-th specialization generated in a program, stage 0's naming.
let reuseSpecializationLabel (name: Str) (index: Int) =
    if index == 0
    then name + "__reuse"
    else name + "__reuse$" + Ashes.Text.fromInt(index)

let recursive specializationCallRoot (expression: Expr) (arguments: List(Expr)) =
    match expression with
        | ExprAt(_span, inner) -> specializationCallRoot(inner)(arguments)
        | ExprCall(function, argument, _isSugar, _layout) -> specializationCallRoot(function)(argument :: arguments)
        | root -> (root, arguments)

let recursive containsCandidateName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | candidate :: rest -> candidate == name || containsCandidateName(name)(rest)

let recursive specializationAccumulatorRecorded (name: Str) (found: List((Str, Str))) =
    match found with
        | [] -> false
        | (candidate, _callee) :: rest -> candidate == name || specializationAccumulatorRecorded(name)(rest)

// The one argument a saturated call to a single-parameter candidate passes, when it is a bare
// parameter name of the enclosing loop.
let recursive unspanSpecializationExpr (expression: Expr) =
    match expression with
        | ExprAt(_span, inner) -> unspanSpecializationExpr(inner)
        | other -> other

let specializationCallAccumulator (parameters: List(Str)) (candidates: List(Str)) (expression: Expr) =
    match specializationCallRoot(expression)([]) with
        | (root, argument :: []) ->
            match (unspanSpecializationExpr(root), unspanSpecializationExpr(argument)) with
                | (ExprVar(callee), ExprVar(accumulator)) ->
                    if containsCandidateName(callee)(candidates) && containsCandidateName(accumulator)(parameters)
                    then Some((accumulator, callee))
                    else None
                | _ -> None
        | _ -> None

// Stage 0's `CollectSpecializableCallArgs`: every loop parameter handed straight to a
// single-parameter specialization candidate as its accumulator, with the callee's name, in the
// order the body first passes it. Stage 0 keeps the LAST such callee per parameter and this keeps
// the first; they agree wherever a parameter reaches only one candidate, which is every shape the
// specialization can be routed for anyway.
let recursive collectSpecializableCallArgs (parameters: List(Str)) (candidates: List(Str)) (expression: Expr) (found: List((Str, Str))) =
    match expression with
        | ExprAt(_span, inner) -> collectSpecializableCallArgs(parameters)(candidates)(inner)(found)
        | ExprCall(function, argument, _isSugar, _layout) as call ->
            found
            |> recordSpecializableCallArg(parameters)(candidates)(call)
            |> collectSpecializableCallArgs(parameters)(candidates)(function)
            |> collectSpecializableCallArgs(parameters)(candidates)(argument)
        | ExprIf(condition, thenBranch, elseBranch) ->
            found
            |> collectSpecializableCallArgs(parameters)(candidates)(condition)
            |> collectSpecializableCallArgs(parameters)(candidates)(thenBranch)
            |> collectSpecializableCallArgs(parameters)(candidates)(elseBranch)
        | ExprLet(_name, value, body, _parameters, _annotation, _constraints) ->
            found
            |> collectSpecializableCallArgs(parameters)(candidates)(value)
            |> collectSpecializableCallArgs(parameters)(candidates)(body)
        | ExprLetRecursive(_name, value, body, _parameters, _annotation, _constraints) ->
            found
            |> collectSpecializableCallArgs(parameters)(candidates)(value)
            |> collectSpecializableCallArgs(parameters)(candidates)(body)
        | ExprMatch(value, cases, _position) ->
            found
            |> collectSpecializableCallArgs(parameters)(candidates)(value)
            |> collectSpecializableCallArgsInCases(parameters)(candidates)(cases)
        | _ -> found
and recordSpecializableCallArg (parameters: List(Str)) (candidates: List(Str)) (call: Expr) (found: List((Str, Str))) =
    match specializationCallAccumulator(parameters)(candidates)(call) with
        | Some((accumulator, callee)) ->
            if specializationAccumulatorRecorded(accumulator)(found)
            then found
            else append(found)([(accumulator, callee)])
        | None -> found
and collectSpecializableCallArgsInCases (parameters: List(Str)) (candidates: List(Str)) (cases: List((Pattern, Expr, Maybe(Expr)))) (found: List((Str, Str))) =
    match cases with
        | [] -> found
        | (_pattern, body, _guard) :: rest ->
            found
            |> collectSpecializableCallArgs(parameters)(candidates)(body)
            |> collectSpecializableCallArgsInCases(parameters)(candidates)(rest)
