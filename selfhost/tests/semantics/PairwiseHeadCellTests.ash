import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
export (
    value runPairwiseHeadCellTests,
)

// A loop that walks a list two elements at a time and conses a pattern-bound head, or a string built
// from two of them, onto its accumulator. The head copy the pattern owner makes carries the source's
// runtime fact, so every cell a loop conses is reference-counted and none comes from the arena.
let source = "let recursive count (xs: List(Str)) (total: Int) =\n    match xs with\n        | [] -> total\n        | _ :: tail -> count(tail)(total + 1)\n\nlet recursive rev (acc: List(Str)) (xs: List(Str)) =\n    match xs with\n        | [] -> acc\n        | head :: tail -> rev(head :: acc)(tail)\n\nlet recursive pairwiseGo (acc: List(Str)) (ps: List(Str)) =\n    match ps with\n        | [] -> rev([])(acc)\n        | first :: rest ->\n            match rest with\n                | [] -> pairwiseGo(first :: acc)([])\n                | second :: more -> pairwiseGo((first + second) :: acc)(more)\n\ncount(pairwiseGo([])([\"a\", \"b\", \"c\"]))(0)\n"

let recursive hasLabel (name: Str) instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Label(candidate) } :: rest -> candidate == name || hasLabel(name)(rest)
        | _ :: rest -> hasLabel(name)(rest)

let recursive countArenaCells instructions =
    match instructions with
        | [] -> 0
        | IrInstruction { instruction = Alloc(_target, 16, false) } :: rest -> 1 + countArenaCells(rest)
        | _ :: rest -> countArenaCells(rest)

let recursive countLoopArenaCells (functions: List(IrFunction)) =
    match functions with
        | [] -> 0
        | function :: rest ->
            (if hasLabel(function.label + "_body")(function.instructions)
            then countArenaCells(function.instructions)
            else 0) + countLoopArenaCells(rest)

let expectPairwiseCellsReferenceCounted unit =
    match parseProgram(source) with
        | ProgramParseResult { program = syntax, diagnostics = [] } ->
            match lowerCoreProgram(syntax) with
                | CoreLoweringResult { program = Some(program), error = None } ->
                    program.functions
                    |> countLoopArenaCells
                    |> test.assertEqual(0)
                | CoreLoweringResult { error = Some(error) } ->
                    error
                    |> Ashes.Trait.Show.show
                    |> (given (text) -> test.fail("the pairwise loop should lower: " + text))
                | _ -> test.fail("the pairwise loop should lower")
        | _ -> test.fail("the pairwise loop should parse")

let runPairwiseHeadCellTests unit = Unit |> expectPairwiseCellsReferenceCounted
