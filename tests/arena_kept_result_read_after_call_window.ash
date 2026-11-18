// expect: TFTF
// A loop function whose Maybe result reaches its owned values only through part duplicates
// keeps the result in the arena; its callers must not take it as reference-counted and reclaim
// the arena before comparing it with None.
import Ashes.Collection.Map
type Ty =
    | TInt
    | TUInt(Int)
    | TList(Ty)
    | TTuple(List(Ty))
    | TFun(Ty, Ty, Maybe(Ty))
    | TVar(Int)
    | TNamed(Int, Str, List(Ty))
    deriving {Eq, Show}

type FnGroup =
    | activeTraitMethods: List((Str, Ty, Str, Str))
    | depth: Int

type St =
    | reversed: List(Int)
    | substitution: MapTree(Int, Ty)
    | nextTemp: Int
    | functionState: FnGroup

let stateActiveTraitMethods (state: St) =
    (let group = state.functionState
    in group.activeTraitMethods)

let recursive applyIn (substitution: MapTree(Int, Ty)) (t: Ty) =
    match t with
        | TVar(id) ->
            match Ashes.Collection.Map.getInt(id)(substitution) with
                | Some(bound) -> applyIn(substitution)(bound)
                | None -> t
        | TList(element) ->
            element
            |> applyIn(substitution)
            |> TList
        | other -> other

let resolveType state semanticType =
    match state with
        | St { substitution = substitution } -> applyIn(substitution)(semanticType)

let recursive activeTraitMethod (traitName: Str) (typeArgument: Ty) (methodName: Str) (state: St) (entries: List((Str, Ty, Str, Str))) =
    match entries with
        | [] -> None
        | (entryTrait, entryType, entryMethod, bindingName) :: rest ->
            if entryTrait == traitName && entryMethod == methodName && resolveType(state)(entryType) == resolveType(state)(typeArgument)
            then Some(bindingName)
            else activeTraitMethod(traitName)(typeArgument)(methodName)(state)(rest)

let sortedTraitMethodNames (traitName: Str) (state: St) =
    if traitName == "z"
    then []
    else [traitName + "m", "q"]

let hasActiveTraitEvidence (traitName: Str) (typeArgument: Ty) (state: St) =
    match sortedTraitMethodNames(traitName)(state) with
        | [] -> false
        | methodName :: _rest ->
            activeTraitMethod(traitName)(typeArgument)(methodName)(state)(stateActiveTraitMethods(state)) != None

let report b =
    if b
    then "T"
    else "F"

let subst =
    Ashes.Collection.Map.empty
    |> Ashes.Collection.Map.setInt(1)(TInt)
    |> Ashes.Collection.Map.setInt(2)(TVar(1))

let st = St(reversed = [1, 2], substitution = subst, nextTemp = 3, functionState = FnGroup(activeTraitMethods = [("Ord", TVar(5), "Ordm", "b1"), ("Eq", TInt, "Eqm", "b2")], depth = 0))

report(hasActiveTraitEvidence("Ord")(TVar(5))(st)) + report(hasActiveTraitEvidence("Ord")(TVar(6))(st)) + report(hasActiveTraitEvidence("Eq")(TVar(2))(st)) + report(hasActiveTraitEvidence("Eq")(TVar(7))(st)) |> Ashes.IO.print
