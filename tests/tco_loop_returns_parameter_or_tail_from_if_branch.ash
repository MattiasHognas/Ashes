// expect: lambda_7 5 lambda_8 4 3000 12
import Ashes.Collection.List.append
import Ashes.Text
type Fn =
    | label: Str
    | instructions: List(Int)
    | localCount: Int
    | tempCount: Int

type State =
    | functions: List(Fn)
    | nextId: Int

let finishFunction label (state: State) =
    match state with
        | State { functions = functions, nextId = nextId } ->
            let function = Fn(label = label, instructions = [nextId + 1, nextId], localCount = nextId, tempCount = nextId * 2)
            in state with functions = append(functions)([function]), nextId = nextId + 1

let lowerOne (state: State) =
    match state with
        | State { nextId = id } ->
            let label = "lambda_" + Ashes.Text.fromInt(id)
            in finishFunction(label)(state)

let recursive lowerMany n (state: State) =
    if n == 0
    then state
    else lowerMany(n - 1)(lowerOne(state))

let recursive count xs =
    match xs with
        | [] -> 0
        | _ :: rest -> 1 + count(rest)

let recursive churn n acc =
    if n == 0
    then acc
    else churn(n - 1)("label_" + Ashes.Text.fromInt(n) :: acc)

let recursive dropWhileSmall (functions: List(Fn)) =
    match functions with
        | [] -> []
        | Fn { localCount = localCount } :: tail ->
            if localCount < 7
            then dropWhileSmall(tail)
            else functions

let recursive dropThroughSmall (functions: List(Fn)) =
    match functions with
        | [] -> []
        | Fn { localCount = localCount } :: tail ->
            if localCount < 7
            then dropThroughSmall(tail)
            else tail

let firstLabel (functions: List(Fn)) =
    match functions with
        | Fn { label = label } :: _ -> label
        | [] -> "none"

let lowered = lowerMany(12)(State(functions = [], nextId = 0))

let suffix =
    match lowered with
        | State { functions = functions } -> dropWhileSmall(functions)

let afterSuffix =
    match lowered with
        | State { functions = functions } -> dropThroughSmall(functions)

let noise = churn(3000)([])

match lowered with
    | State { functions = functions } -> Ashes.IO.print(firstLabel(suffix) + " " + Ashes.Text.fromInt(count(suffix)) + " " + firstLabel(afterSuffix) + " " + Ashes.Text.fromInt(count(afterSuffix)) + " " + Ashes.Text.fromInt(count(noise)) + " " + Ashes.Text.fromInt(count(functions)))
