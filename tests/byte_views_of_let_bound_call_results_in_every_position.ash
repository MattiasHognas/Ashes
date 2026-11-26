// expect: 1zzzzzzzzzzzz72zzzzzzzzzzzz143zzzzzzzzzzzz21#ab1cd|x1yz,x2yz,x3yz,|cl2os|pa3rm|ge4ne|tu5pl
// The bytes of a let-bound call result in every position a view can escape its owner's scope.
type Holder =
    | v: Bytes

let id (text: Str) = text + ""

let showView (v: Bytes) =
    v
    |> Ashes.Byte.length
    |> Ashes.Byte.subText(v)(0)

let inRecord (n: Int) =
    (let s = id("ab" + Ashes.Text.fromInt(n) + "cd")
    in
        let w = Ashes.Byte.fromText(s)
        in Holder(v = w))

let recursive accumulate (i: Int) (acc: List(Bytes)) =
    if i == 0
    then acc
    else
        let s = id("x" + Ashes.Text.fromInt(i) + "yz")
        in
            let w = Ashes.Byte.fromText(s)
            in accumulate(i - 1)(w :: acc)

let inClosure (n: Int) =
    (let s = id("cl" + Ashes.Text.fromInt(n) + "os")
    in
        let w = Ashes.Byte.fromText(s)
        in
            given (_u: Int) -> w)

let ownedByParameter (s: Str) = Ashes.Byte.fromText(s)

let keep x = x

let throughGeneric (n: Int) =
    (let s = id("ge" + Ashes.Text.fromInt(n) + "ne")
    in
        let w = Ashes.Byte.fromText(s)
        in keep(w))

let inTuple (n: Int) =
    (let s = id("tu" + Ashes.Text.fromInt(n) + "pl")
    in
        let w = Ashes.Byte.fromText(s)
        in (1, w))

let recursive showAll (views: List(Bytes)) (acc: Str) =
    match views with
        | [] -> acc
        | v :: rest -> showAll(rest)(acc + showView(v) + ",")

let noise (n: Int) = Ashes.Text.fromInt(n) + "zzzzzzzzzzzz" + Ashes.Text.fromInt(n * 7)

let r = inRecord(1)

let a = accumulate(3)([])

let c = inClosure(2)

let p =
    "pa" + Ashes.Text.fromInt(3) + "rm"
    |> id
    |> ownedByParameter

let g = throughGeneric(4)

let tp = inTuple(5)

Ashes.IO.print(noise(1) + noise(2) + noise(3) + "#" + showView(r.v) + "|" + showAll(a)("") + "|" + showView(c(0)) + "|" + showView(p) + "|" + showView(g) + "|" + (match tp with
    | (_n, w) -> showView(w)))
