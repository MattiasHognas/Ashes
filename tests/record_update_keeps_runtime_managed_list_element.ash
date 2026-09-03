// expect: Third, ,Slot=1,None,Target=0 Third,
import Ashes.Collection.List
type Inst =
    | text: Str
    | n: Int

type Body =
    | items: List(Inst)
    | count: Int

let emitTo (inst: Inst) (body: Body) = body with items = [inst]

let mk (label: Str) = Inst(text = "" + label, n = 1)

let joinAll (items: List(Str)) =
    Ashes.Collection.List.foldLeft(given (acc) ->
        given (item) -> acc + "," + item)("")(items)

let build (seed: Str) =
    Body(items = [], count = 0)
    |> emitTo(mk(seed))
    |> emitTo(mk("Second"))
    |> emitTo(mk("Third"))

let recursive texts (items: List(Inst)) =
    match items with
        | [] -> ""
        | Inst { text = text } :: rest -> text + "," + texts(rest)

let describe (body: Body) =
    match body with
        | Body { items = items } -> texts(items) + " " + joinAll(["Slot=1", "None", "Target=0"]) + " " + texts(items)

"Found"
|> build
|> describe
|> Ashes.IO.print
