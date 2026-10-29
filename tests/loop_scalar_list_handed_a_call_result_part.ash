// expect: 762
let produce (k: Int) = Some(([k, k + 1], [k * 2]))

let recursive handOn (remaining: Int) (acc: List(Maybe((List(Int), List(Int))))) (other: List(Int)) (total: Int) =
    if remaining == 0
    then total + Ashes.Collection.List.length(other)
    else
        match produce(remaining) with
            | Some((a, b)) -> handOn(remaining - 1)([Some((other, b))])(a)(total + 1)
            | None -> handOn(remaining - 1)(acc)(other)(total)

let checked (k: Int) =
    if k < 0
    then Error("negative")
    else Ok(([k, k + 1, k + 2], [k * 2]))

let recursive siblings (remaining: Int) (first: List(Int)) (second: List(Int)) (total: Int) =
    if remaining == 0
    then total + Ashes.Collection.List.length(first) + Ashes.Collection.List.length(second)
    else
        match checked(remaining) with
            | Ok((a, b)) -> siblings(remaining - 1)(b)(a)(total + Ashes.Collection.List.length(first))
            | Error(_) -> siblings(remaining - 1)(first)(second)(total)

Ashes.IO.print(handOn(300)([])([7])(0) + siblings(456)([7])([8, 9])(0))
