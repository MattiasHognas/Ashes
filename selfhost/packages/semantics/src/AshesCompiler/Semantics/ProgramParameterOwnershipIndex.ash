import AshesCompiler.Semantics.OwnershipSummary.ParameterOwnership
import Ashes.Collection.Map.MapTree
export (
    type ProgramParameterOwnershipIndex,
    value indexProgramParameterOwnership,
    value lookupIndexedProgramParameterOwnership,
)

// This lookup preserves the first table entry. It is not the fixpoint's unique-name
// index: duplicate names here must keep the old linear lookup's exact answer.
type alias ProgramParameterOwnershipIndex = MapTree(Str, List((Str, ParameterOwnership)))

let recursive indexEntries (entries: List((Str, List((Str, ParameterOwnership))))) (index: ProgramParameterOwnershipIndex) =
    match entries with
        | [] -> index
        | (name, ownership) :: rest ->
            match Ashes.Collection.Map.getStr(name)(index) with
                | Some(_first) -> indexEntries(rest)(index)
                | None ->
                    index
                    |> Ashes.Collection.Map.setStr(name)(ownership)
                    |> indexEntries(rest)

let indexProgramParameterOwnership (entries: List((Str, List((Str, ParameterOwnership))))) = indexEntries(entries)(Ashes.Collection.Map.empty)

let lookupIndexedProgramParameterOwnership (name: Str) (index: ProgramParameterOwnershipIndex) = Ashes.Collection.Map.getStr(name)(index)
