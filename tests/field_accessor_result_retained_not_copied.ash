// expect: 1234 56 110 4
type Holder =
    | holderBytes: Bytes
    | holderText: Str
    | holderCount: Int

let textOf (holder: Holder) = holder.holderText

let bytesOf (holder: Holder) = holder.holderBytes

let recursive total (holder: Holder) (index: Int) (count: Int) (sum: Int) =
    if index >= count
    then sum
    else
        total(holder)(index + 1)(count)(sum + Ashes.Text.byteLength(textOf(holder)) + Ashes.Byte.length(bytesOf(holder)))

let makeHolder (n: Int) =
    Holder(holderBytes = n * 1000
    |> Ashes.Text.fromInt
    |> Ashes.Byte.fromText, holderText = Ashes.Text.fromInt(n), holderCount = n)

let held = makeHolder(1234)

let named = textOf(held)

let fresh =
    56
    |> makeHolder
    |> textOf

Ashes.IO.print(named + " " + fresh + " " + Ashes.Text.fromInt(total(held)(0)(10)(0)) + " " + Ashes.Text.fromInt(7
|> makeHolder
|> bytesOf
|> Ashes.Byte.length))
