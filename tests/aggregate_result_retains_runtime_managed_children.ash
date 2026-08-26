// expect: [1, 2, 3] [1, 2, 3] | [1, 2, 3] [1, 2, 3, 4] | [[1, 2, 3], [1, 2, 3]] | [0, 1, 2, 3] [1, 2, 3] | [0, 1, 2, 3] [4, 5] | 2000
import Ashes.IO
type Inst =
    | Cat(Int, Int, Int, Bool)
    | Load(Int)

let recursive count (xs: List(Int)) (acc: Int) =
    match xs with
        | [] -> acc
        | _ :: rest -> count(rest)(acc + 1)

let recursive churn (n: Int) acc =
    if n == 0
    then acc
    else churn(n - 1)(n :: acc)

let describe (result: (List(Int), List(Int))) =
    match result with
        | (xs, ys) -> Ashes.Trait.Show.show(xs) + " " + Ashes.Trait.Show.show(ys)

let recursive twoAccumulators (n: Int) xs ys =
    if n == 0
    then (xs, ys)
    else twoAccumulators(n - 1)(n :: xs)(n :: ys)

let letBoundPair (n: Int) =
    (let ys = churn(n)([])
    in
        let zs = churn(n + 1)([])
        in (ys, zs))

let recursive listOfAccumulators (n: Int) xs ys =
    if n == 0
    then [xs, ys]
    else listOfAccumulators(n - 1)(n :: xs)(n :: ys)

let recursive consInsideTuple (n: Int) xs ys =
    if n == 0
    then (0 :: xs, ys)
    else consInsideTuple(n - 1)(n :: xs)(n :: ys)

let recursive lookupAssociation key entries =
    match entries with
        | [] -> None
        | (k, v) :: tail ->
            if k == key
            then Some(v)
            else lookupAssociation(key)(tail)

let recursive walkChain defs (left: Int) (managed: Bool) rights absorbed =
    match lookupAssociation(left)(defs) with
        | Some(Cat(t, l, r, m)) ->
            if m == managed
            then walkChain(defs)(l)(managed)(r :: rights)(t :: absorbed)
            else (left :: rights, absorbed)
        | _ -> (left :: rights, absorbed)

let defs = [(4, Cat(4)(0)(1)(false)), (5, Cat(5)(4)(2)(false)), (0, Load(0))]

let first = twoAccumulators(3)([])([])

let second = letBoundPair(3)

let third = listOfAccumulators(3)([])([])

let fourth = consInsideTuple(3)([])([])

let fifth = walkChain(defs)(5)(false)([3])([])

let noise = churn(2000)([])

print(describe(first) + " | " + describe(second) + " | " + Ashes.Trait.Show.show(third) + " | " + describe(fourth) + " | " + describe(fifth) + " | " + Ashes.Trait.Show.show(count(noise)(0)))
