// An accumulator of (key, value) pairs whose value is a recursive type: the loop conses a pair
// built from a key matched out of the walked list and a call's owned result, or hands the
// accumulator on. The list is placed on the reference-counted heap and its successor copied at the
// back edge, so the arena pair borrows the key and no key outlives the round.
// expect: passed
type Ty =
    | SemInt
    | SemUInt(Int)
    | SemFloat
    | SemString
    | TBool
    | SemNever
    | TList(Ty)
    | SemTuple(List(Ty))
    | TFun(Ty, Ty, Maybe(Ty))
    | TVar(Int)
    | SemCapability(Str, List(Ty))
    | SemRow(List(Ty), Maybe(Ty))
    | TNamed(Int, Str, List(Ty))
    | SemParameter(Int, Str)
    | SemOpaque(Str)
    | SemPointer(Ty)

type St =
    | pending: List((Str, Ty))
    | bound: Int

let resolve (state: St) (t: Ty) =
    match t with
        | TVar(n) ->
            if n == state.bound
            then TBool
            else TVar(n)
        | other -> other

let recursive free (t: Ty) =
    match t with
        | TBool -> []
        | TVar(n) -> [n]
        | TList(inner) -> free(inner)
        | TNamed(_id, _name, _arguments) -> []
        | TFun(_left, right, _effects) -> free(right)
        | _ -> []

let recursive collect (pending: List((Str, Ty))) (state: St) (resolved: List((Str, Ty))) =
    match pending with
        | [] -> resolved
        | (key, goalType) :: rest ->
            let goalResolved = resolve(state)(goalType)
            in
                match free(goalResolved) with
                    | [] -> collect(rest)(state)((key, goalResolved) :: resolved)
                    | _variables -> collect(rest)(state)(resolved)

let withPending value (state: St) = state with pending = value

let seed n = St(pending = [], bound = n + 1)

let direct n =
    (let entered = seed(n)
    in
        let key = Ashes.Text.fromInt(n) + ":Not:not"
        in
            let state = withPending([(key, TVar(n + 1))])(entered)
            in
                let goals = collect(state.pending)(state)([])
                in Ashes.Collection.List.length(goals) == 1)

let recursive rounds n =
    if n == 0
    then Ashes.IO.print("passed")
    else
        if direct(n)
        then rounds(n - 1)
        else Ashes.IO.panic("bad result")

rounds(200)
