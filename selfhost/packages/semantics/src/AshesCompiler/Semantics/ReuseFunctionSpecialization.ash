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

import AshesCompiler.Frontend.Syntax
export (
    value reuseSpecializationCandidates,
    value reuseSpecializationLabel,
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
