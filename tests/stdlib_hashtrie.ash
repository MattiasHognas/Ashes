// expect: 15 20 0 2 35 sum=499500 n=1000
import Ashes.IO
import Ashes.HashTrie
import Ashes.Text
let t1 = 
    Ashes.HashTrie.upsertHashed(Ashes.HashTrie.hashText("alpha"))("alpha")(10)(given (old) -> old + 1)(Ashes.HashTrie.empty)

let t2 = 
    Ashes.HashTrie.upsertHashed(Ashes.HashTrie.hashText("beta"))("beta")(20)(given (old) -> old + 1)(t1)

let t3 = 
    Ashes.HashTrie.upsertHashed(Ashes.HashTrie.hashText("alpha"))("alpha")(99)(given (old) -> old + 5)(t2)

let a = 
    match Ashes.HashTrie.getHashed(Ashes.HashTrie.hashText("alpha"))("alpha")(t3) with
        | Some(v) -> v
        | None -> -1

let b = 
    match Ashes.HashTrie.getHashed(Ashes.HashTrie.hashText("beta"))("beta")(t3) with
        | Some(v) -> v
        | None -> -1

let missing = 
    match Ashes.HashTrie.getHashed(Ashes.HashTrie.hashText("gamma"))("gamma")(t3) with
        | Some(_v) -> 1
        | None -> 0

let total = 
    Ashes.HashTrie.foldLeft(given (acc) -> 
        given (_k) -> 
            given (v) -> acc + v)(0)(t3)

let recursive fill i acc = 
    if i >= 1000
    then acc
    else 
        let key = "station-" + Ashes.Text.fromInt(i)
        in 
            fill(i + 1)(Ashes.HashTrie.upsertHashed(Ashes.HashTrie.hashText(key))(key)(i)(given (old) -> old + 1000000)(acc))

let big = fill(0)(Ashes.HashTrie.empty)

let bigSum = 
    Ashes.HashTrie.foldLeft(given (acc) -> 
        given (_k) -> 
            given (v) -> acc + v)(0)(big)

Ashes.IO.writeLine(Ashes.Text.fromInt(a) + " " + Ashes.Text.fromInt(b) + " " + Ashes.Text.fromInt(missing) + " " + Ashes.Text.fromInt(Ashes.HashTrie.size(t3)) + " " + Ashes.Text.fromInt(total) + " sum=" + Ashes.Text.fromInt(bigSum) + " n=" + Ashes.Text.fromInt(Ashes.HashTrie.size(big)))
