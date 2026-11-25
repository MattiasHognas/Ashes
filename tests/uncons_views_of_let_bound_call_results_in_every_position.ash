// expect: 1zzzzzzzzzzzz72zzzzzzzzzzzz143zzzzzzzzzzzz21#b1cd|1yz,2yz,3yz,|l2os|a3rm|e4ne|u5pl
// The tail of an uncons of a let-bound call result, bound by a `let` and matched later, reaching
// a returned record, an accumulator, a returned closure, a parameter-owned result, a generic
// helper and a returned tuple: each is copied or keeps its backing alive.
type Holder =
    | v: Str

let id (text: Str) = text + ""

let inRecord (n: Int) =
    (let s = id("ab" + Ashes.Text.fromInt(n) + "cd")
    in
        let parts = Ashes.Text.unconsText(s)
        in
            match parts with
                | Some((_h, t)) -> Holder(v = t)
                | None -> Holder(v = ""))

let recursive accumulate (i: Int) (acc: List(Str)) =
    if i == 0
    then acc
    else
        let s = id("x" + Ashes.Text.fromInt(i) + "yz")
        in
            let parts = Ashes.Text.unconsText(s)
            in
                match parts with
                    | Some((_h, t)) -> accumulate(i - 1)(t :: acc)
                    | None -> acc

let inClosure (n: Int) =
    (let s = id("cl" + Ashes.Text.fromInt(n) + "os")
    in
        let parts = Ashes.Text.unconsText(s)
        in
            match parts with
                | Some((_h, t)) ->
                    given (_u: Int) -> t
                | None ->
                    given (_u: Int) -> "")

let ownedByParameter (s: Str) =
    (let parts = Ashes.Text.unconsText(s)
    in
        match parts with
            | Some((_h, t)) -> t
            | None -> "")

let keep x = x

let throughGeneric (n: Int) =
    (let s = id("ge" + Ashes.Text.fromInt(n) + "ne")
    in
        let parts = Ashes.Text.unconsText(s)
        in
            match parts with
                | Some((_h, t)) -> keep(t)
                | None -> "")

let inTuple (n: Int) =
    (let s = id("tu" + Ashes.Text.fromInt(n) + "pl")
    in
        let parts = Ashes.Text.unconsText(s)
        in
            match parts with
                | Some((h, t)) -> (h, t)
                | None -> ("", ""))

let recursive showAll (views: List(Str)) (acc: Str) =
    match views with
        | [] -> acc
        | v :: rest -> showAll(rest)(acc + v + ",")

let noise (n: Int) = Ashes.Text.fromInt(n) + "zzzzzzzzzzzz" + Ashes.Text.fromInt(n * 7)

let r = inRecord(1)

let a = accumulate(3)([])

let c = inClosure(2)

let p = ownedByParameter("pa" + Ashes.Text.fromInt(3) + "rm")

let g = throughGeneric(4)

let tp = inTuple(5)

Ashes.IO.print(noise(1) + noise(2) + noise(3) + "#" + r.v + "|" + showAll(a)("") + "|" + c(0) + "|" + p + "|" + g + "|" + (match tp with
    | (_h, t) -> t))
