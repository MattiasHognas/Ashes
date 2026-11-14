// An accumulator of (key, value) pairs seeded with a list its caller keeps and reads again: each
// successor copies only the cell consed this iteration and shares the seed as its suffix, so the
// seed survives both loops and every key is released once.
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

let recursive keyLength (entries: List((Str, Ty))) (total: Int) =
    match entries with
        | [] -> total
        | (key, _goalType) :: rest -> keyLength(rest)(total + Ashes.Text.byteLength(key))

let direct n =
    (let entered = seed(n)
    in
        let key = Ashes.Text.fromInt(n) + ":Not:not"
        in
            let base = [(Ashes.Text.fromInt(n) + ":base", TList(TVar(n))), (key + ":second", TBool)]
            in
                let state = withPending([(key, TVar(n + 1)), (key + "x", TVar(n + 1)), (key + "y", TVar(n))])(entered)
                in
                    let goals = collect(state.pending)(state)(base)
                    in
                        let again = collect(state.pending)(state)(base)
                        in keyLength(goals)(0) + keyLength(again)(0) + keyLength(base)(0) > 0 && Ashes.Collection.List.length(goals) == 4)

let recursive rounds n =
    if n == 0
    then Ashes.IO.print("passed")
    else
        if direct(n)
        then rounds(n - 1)
        else Ashes.IO.panic("bad result")

rounds(200)
