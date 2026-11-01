import Ashes.IO
import Ashes.Collection.List as L
import Ashes.Core.Maybe.map as mapMaybe
type Color =
    | Red
    | Green(Int)
    | Blue(Str, Int)

type alias Name = Str

type UserId = UserId(Int)

type Point =
    | x: Int
    | y: Int

let answer = 42

let big = 123456789012345678901234567890N

let ratio = 1.5

let letter = 'a'

let flag = true

let label : Name = "hi"

let add a b = a + b

let scale (p: Point) k = Point(x = p.x * k, y = p.y * k)

let origin = Point(x = 0, y = 0)

let moved = origin with x = 3

let pair = (1, "one")

let nums = [1, 2, 3]

let more = 0 :: nums

let uid = UserId(7)

let unwrap id =
    match id with
        | UserId(value) -> value

let recursive even n =
    if n == 0
    then true
    else odd(n - 1)
and odd n =
    if n == 0
    then false
    else even(n - 1)

let describe c =
    match c with
        | Red -> "red"
        | Green(n) when n > 10 -> "big green"
        | Green(n) -> "green"
        | Blue(s, _) as whole -> s

let sumList xs =
    L.foldLeft(given (acc) ->
        given (v) -> acc + v)(0)(xs)

let lam =
    given (z) -> z * 2

let piped = nums |> L.map(lam)

let bits = answer & 7 | 1 << 2 ^ ~3 >> 1

let byte = 255u8

let logic = !flag && (answer >= 3 || answer != 4)

let local =
    match pair with
        | (q, r) -> q

let headOr xs =
    match xs with
        | [] -> 0
        | h :: _ -> h

let orPat c =
    match c with
        | Red | Green(_) -> 1
        | Blue(_, _) -> 2

let pointPat p =
    match p with
        | Point { x = px, y = _ } -> px

let maybeDouble =
    mapMaybe(given (v) -> v * 2)(Some(3))

let resultChain =
    Ok(1)
    |?> (given (v) -> v + 1)
    |!> (given (e) -> e + "!")

Ashes.IO.print(add(answer)(unwrap(uid)) + sumList(more) + local + headOr(piped) + bits)
