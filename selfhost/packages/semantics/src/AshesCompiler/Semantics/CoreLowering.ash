// Lowers the strict functional core into semantic IR before ownership placement.
//
// Invariants:
// - Callees are evaluated before arguments, and curried arguments are applied one at a time.
// - Conditions and match scrutinees are evaluated once; guarded arms retain source order.
// - Every function owns independent temp/local counters; closure env and argument locals are 0 and 1.
// - Captures follow first free-use order and occupy consecutive eight-byte environment words.
// - Recursive self/sibling references rebuild closures from the current shared environment.
// - Lifted functions retain generation order, with nested functions preceding their enclosing function.
// - Every entry point here lowers a numbered program from the types the standalone inference
//   recorded against it. The raw entry points, which lower without them, are in `CoreLowering.Raw`
//   and are test-only.

import Ashes.Collection.Map.MapTree
import AshesCompiler.Frontend.Syntax.Expr
import AshesCompiler.Frontend.Syntax.ProgramSyntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.NodeTypeTable.NodeTypeTable
import AshesCompiler.Semantics.CoreBuiltinLowering
import AshesCompiler.Semantics.SourceContext
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.MutualRecursionDispatch
import AshesCompiler.Semantics.ReuseFunctionSpecialization
import AshesCompiler.Semantics.CoreLowering.Model
import AshesCompiler.Semantics.CoreLowering.LoweringState
import AshesCompiler.Semantics.CoreLowering.Emit
import AshesCompiler.Semantics.CoreLowering.RecursiveGroups
import AshesCompiler.Semantics.CoreLowering.Expressions
import AshesCompiler.Semantics.CoreLowering.Program
// A numbered program lowered from the types the standalone inference recorded against it, as
// lowerCoreNumberedProgramWithSourceAndReuse takes them.
let lowerCoreNumberedProgram (program: ProgramSyntax) (nodeTypes: NodeTypeTable) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let trailingBody =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprVar("Unit")
            in
                Unit
                |> initialState
                |> (given (state: CoreLoweringState) ->
                    state
                    |> withStateTopLevelNames(allTopLevelBindingNames(items)(Ashes.Collection.Map.empty))
                    |> withStateSpecializationCandidates(reuseSpecializationCandidates(items))
                    |> withNodeTypes(nodeTypes))
                |> withProgramParameterOwnership(program)
                |> lowerProgramPasses(items)(trailingBody)(None)
                |> buildProgram

let withSourceRegions (regions: SourceRegions) (state: CoreLoweringState) =
    state
    |> withStateSourceContext(Some(regions.context))
    |> withStateSourceRegions(regions.regionOfName)
    |> withStateSourceRegionOf("")

// A program the standalone inference ran on first, with source locations and the reuse switch: its
// items are the numbered units with `deriving` already expanded, every expression node is wrapped
// in its identity, and `nodeTypes` holds what the inference recorded against those identities.
let lowerNumberedProgramWithRegionsAndReuse (reuseEnabled: Bool) (regions: SourceRegions) (nodeTypes: NodeTypeTable) (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            let trailingBody =
                match body with
                    | Some(expression) -> expression
                    | None -> ExprVar("Unit")
            in
                Unit
                |> initialStateWithContext(standardConstructorLayouts)(standardBuiltinLayouts)
                |> (given (state: CoreLoweringState) ->
                    state
                    |> withSourceRegions(regions)
                    |> withStateTopLevelNames(allTopLevelBindingNames(items)(Ashes.Collection.Map.empty))
                    |> withStateSpecializationCandidates(reuseSpecializationCandidates(items))
                    |> withStateReuseEnabled(reuseEnabled)
                    |> withNodeTypes(nodeTypes))
                |> withProgramParameterOwnership(program)
                |> lowerProgramPasses(items)(trailingBody)(None)
                |> buildProgram

let lowerNumberedProgramWithSourceAndReuse (reuseEnabled: Bool) (filePath: Str) (source: Str) (nodeTypes: NodeTypeTable) (program: ProgramSyntax) =
    lowerNumberedProgramWithRegionsAndReuse(reuseEnabled)(singleSourceRegions(filePath)(source))(nodeTypes)(program)

// As lowerCoreNumberedProgramWithSourceAndReuse for a stitched program whose spans resolve in their
// own files' texts.
let lowerCoreNumberedProgramWithRegionsAndReuse (reuseEnabled: Bool) (regions: SourceRegions) (program: ProgramSyntax) (nodeTypes: NodeTypeTable) =
    match mergeMutualRecursionGroups(program) with
        | None -> lowerNumberedProgramWithRegionsAndReuse(reuseEnabled)(regions)(nodeTypes)(program)
        | Some(merged) ->
            match lowerNumberedProgramWithRegionsAndReuse(reuseEnabled)(regions)(nodeTypes)(merged) with
                | CoreLoweringResult { error = None } as lowered -> lowered
                | _failed -> lowerNumberedProgramWithRegionsAndReuse(reuseEnabled)(regions)(nodeTypes)(program)

// A numbered single-file program and the types inference recorded against it, with source
// locations and the reuse switch. A program with a mutually recursive group that merges into one
// dispatch loop is lowered in its merged form; the merge does not know the members' types, so a
// merged program that fails to lower gives way to the program as written.
let lowerCoreNumberedProgramWithSourceAndReuse (reuseEnabled: Bool) (filePath: Str) (source: Str) (program: ProgramSyntax) (nodeTypes: NodeTypeTable) =
    match mergeMutualRecursionGroups(program) with
        | None -> lowerNumberedProgramWithSourceAndReuse(reuseEnabled)(filePath)(source)(nodeTypes)(program)
        | Some(merged) ->
            match lowerNumberedProgramWithSourceAndReuse(reuseEnabled)(filePath)(source)(nodeTypes)(merged) with
                | CoreLoweringResult { error = None } as lowered -> lowered
                | _failed -> lowerNumberedProgramWithSourceAndReuse(reuseEnabled)(filePath)(source)(nodeTypes)(program)
