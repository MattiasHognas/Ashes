// expect: passed
import Ashes.Collection.Map
type Packet =
    | empty
    | Packet(MapTree(Str, List(Str)))

let identity packet = packet

let recursive walk remaining packet =
    if remaining == 0
    then
        match packet with
            | Packet(values) ->
                match Ashes.Collection.Map.getStr("key")(values) with
                    | Some(value :: []) -> Ashes.IO.print(value)
                    | _ -> Ashes.IO.panic("lost payload")
            | _ -> Ashes.IO.panic("lost wrapper")
    else
        match identity(packet) with
            | Packet(_values) as empty -> walk(remaining - 1)(empty)
            | empty -> Ashes.IO.panic("empty packet")

Ashes.Collection.Map.empty
|> Ashes.Collection.Map.setStr("key")(["passed"])
|> Packet
|> walk(20)
