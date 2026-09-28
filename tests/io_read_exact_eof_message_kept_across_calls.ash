// stdin: hi\n
// expect: hi|readExact: unexpected EOF|readExact: unexpected EOF|readExact: unexpected EOF|42

let readMessage (n: Int) =
    match Ashes.IO.readExact(n) with
        | Ok(text) -> text
        | Error(message) -> message

let recursive gather (n: Int) (acc: List(Str)) =
    if n == 0
    then acc
    else gather(n - 1)(readMessage(4) :: acc)

let recursive noise (k: Int) (acc: Str) =
    if k == 0
    then acc
    else noise(k - 1)(acc + Ashes.Text.fromInt(k * 7919))

let first = readMessage(2)

let messages = gather(3)([])

let spill = noise(20)("")

Ashes.Text.join("|")(Ashes.Collection.List.reverse(messages)) + "|" + Ashes.Text.fromInt(Ashes.Text.byteLength(spill) - 65)
|> (given (rest) -> first + "|" + rest)
|> Ashes.IO.print
