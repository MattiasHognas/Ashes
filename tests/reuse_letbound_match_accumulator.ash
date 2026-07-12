// expect: 30000|777485456
// CO-16: a `let m2 = match/if ... in loop(m2)` accumulator is now recognized as address-stable for
// the loop arena reset, so the natural (bind-then-tail-call) fold shape is constant-memory instead
// of leaking (117 MB -> 8.6 MB at 1M inserts before/after). The reset-safety check now traces a
// let-bound accumulator back through its binding and requires every match/if leaf to be stable, with
// shadow tracking so a pattern binder (here Odd(kk)) that coincides with the accumulator name is not
// mistaken for it. Inserts 300k growing string keys (bounded 30k keyspace, half via a match arm that
// binds kk), then reads back a spread and sums values; a reset-driven use-after-free would corrupt a
// stored value and change the checksum, which is computed independently.
import Ashes.Map
import Ashes.Text
import Ashes.IO
type Choice =
    | Even
    | Odd(Int)

let recursive build i n m =
    if i >= n
    then m
    else
        let k = i - i / 30000 * 30000
        in
            let tag =
                if k - k / 2 * 2 == 0
                then Even
                else Odd(k)
            in
                let m2 =
                    match tag with
                        | Even -> Ashes.Map.setStr(Ashes.Text.fromInt(k))(i)(m)
                        | Odd(kk) -> Ashes.Map.setStr(Ashes.Text.fromInt(kk))(i + 7)(m)
                in build(i + 1)(n)(m2)

let recursive checksum i n m acc =
    if i >= n
    then acc
    else
        let v =
            match Ashes.Map.getStr(Ashes.Text.fromInt(i))(m) with
                | Some(x) -> x
                | None -> -1
        in checksum(i + 11)(n)(m)(acc + v)

let n = 300000

let built = build(0)(n)(Ashes.Map.empty)
in Ashes.IO.print(Ashes.Text.fromInt(Ashes.Map.size(built)) + "|" + Ashes.Text.fromInt(checksum(0)(30000)(built)(0)))
