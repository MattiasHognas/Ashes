// expect: Third, Third,
type Inst =
    | text: Str
    | n: Int

type Body =
    | items: List(Inst)
    | count: Int

let emitTo (body: Body) (inst: Inst) = body with items = [inst]

let mk (label: Str) = Inst(text = "" + label, n = 1)

let build (seed: Str) =
    "Third"
    |> mk
    |> emitTo("Second"
    |> mk
    |> emitTo(seed
    |> mk
    |> emitTo(Body(items = [], count = 0))))

let recursive texts (items: List(Inst)) =
    match items with
        | [] -> ""
        | Inst { text = text } :: rest -> text + "," + texts(rest)

let describe (body: Body) =
    match body with
        | Body { items = items } -> texts(items) + " " + texts(items)

"Found"
|> build
|> describe
|> Ashes.IO.print
