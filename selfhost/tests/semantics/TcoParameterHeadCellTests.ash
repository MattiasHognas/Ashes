import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
export (
    value runTcoParameterHeadCellTests,
)

// A loop that walks one list by its tail and conses a string parameter onto another. The walked
// list's predecessor is released at each back edge, so the loop's entry normalizes the list it is
// given and its closure says so; the cell consed onto the accumulator is reference-counted.
let source = "let recursive count (xs: List(Str)) (total: Int) =\n    match xs with\n        | [] -> total\n        | _ :: tail -> count(tail)(total + 1)\n\nlet recursive stamp (separator: Str) (acc: List(Str)) (ps: List(Str)) =\n    match ps with\n        | [] -> acc\n        | _ :: rest -> stamp(separator)(separator :: acc)(rest)\n\ncount(stamp(\",\")([])([\"a\", \"b\"]))(0)\n"

let recursive hasLabel (name: Str) instructions =
    match instructions with
        | [] -> false
        | IrInstruction { instruction = Label(candidate) } :: rest -> candidate == name || hasLabel(name)(rest)
        | _ :: rest -> hasLabel(name)(rest)

let recursive closureAccepts (label: Str) instructions =
    match instructions with
        | [] -> None
        | IrInstruction { instruction = MakeClosure(_target, callee, _environment, _size, _managed, _returns, accepts, _owned) } :: rest ->
            if callee == label
            then Some(accepts)
            else closureAccepts(label)(rest)
        | _ :: rest -> closureAccepts(label)(rest)

let recursive closureAcceptsIn (label: Str) (functions: List(IrFunction)) =
    match functions with
        | [] -> None
        | function :: rest ->
            match closureAccepts(label)(function.instructions) with
                | Some(accepts) -> Some(accepts)
                | None -> closureAcceptsIn(label)(rest)

let recursive countCells (runtimeManaged: Bool) instructions =
    match instructions with
        | [] -> 0
        | IrInstruction { instruction = Alloc(_target, 16, managed) } :: rest ->
            (if managed == runtimeManaged
            then 1
            else 0) + countCells(runtimeManaged)(rest)
        | _ :: rest -> countCells(runtimeManaged)(rest)

let recursive stampLoop (functions: List(IrFunction)) =
    match functions with
        | [] -> None
        | function :: rest ->
            if hasLabel(function.label + "_body")(function.instructions) && countCells(true)(function.instructions) + countCells(false)(function.instructions) > 0
            then Some(function)
            else stampLoop(rest)

let expectWalkedListNormalizedAtEntry unit =
    match parseProgram(source) with
        | ProgramParseResult { program = syntax, diagnostics = [] } ->
            match lowerCoreProgram(syntax) with
                | CoreLoweringResult { program = Some(program), error = None } ->
                    match stampLoop(program.functions) with
                        | None -> test.fail("the stamp loop should lower to a loop building cells")
                        | Some(loop) ->
                            loop.instructions
                            |> countCells(false)
                            |> test.assertEqual(0)
                            |> (given (_) ->
                                match closureAcceptsIn(loop.label)(program.functions) with
                                    | Some(accepts) -> test.assertEqual(true)(accepts)
                                    | None -> test.fail("the stamp loop's closure should be made"))
                | _ -> test.fail("the stamp loop should lower")
        | _ -> test.fail("the stamp loop should parse")

let runTcoParameterHeadCellTests unit = Unit |> expectWalkedListNormalizedAtEntry
