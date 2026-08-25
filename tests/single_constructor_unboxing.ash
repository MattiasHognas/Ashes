// expect: 5000250000 bob 42 24 3 12 3 !!!
import Ashes.Collection.List as list
type Point =
    | Point(Int, Int)

type Person =
    | name: Str
    | age: Int

type Shape =
    | Circle(Int)
    | Rect(Int, Int)

type Pair =
    | Pair(Int, Int)

type Wrapped =
    | Wrapped(List(Int), Str)

let manhattan p =
    match p with
        | Point(x, y) -> x + y

let older : Person -> Person =
    given (person) -> person with age = person.age + 1

let area shape =
    match shape with
        | Circle(r) -> r * r * 3
        | Rect(w, h) -> w * h

let recursive walk k p acc =
    if k == 0
    then acc
    else
        match p with
            | Point(x, y) -> walk(k - 1)(Point(y)(x + 1))(acc + manhattan(p))

let recursive bumpAll pairs =
    match pairs with
        | [] -> []
        | Pair(a, b) :: rest -> Pair(b)(a + 1) :: bumpAll(rest)

let recursive sumFirst pairs acc =
    match pairs with
        | [] -> acc
        | Pair(a, _) :: rest -> sumFirst(rest)(acc + a)

let recursive countDown k w =
    if k == 0
    then w
    else
        match w with
            | Wrapped(items, label) -> countDown(k - 1)(Wrapped(k :: items)(label + "!"))

let describe w =
    match w with
        | Wrapped(items, label) -> Ashes.Text.fromInt(list.length(items)) + " " + label

let bob = Person(name = "bob", age = 41)

let bob2 = older(bob)

let pairs = bumpAll(bumpAll([Pair(1)(2), Pair(3)(4), Pair(5)(6)]))

let wrapped = countDown(3)(Wrapped([])(""))

Ashes.IO.print(
    Ashes.Text.fromInt(walk(100000)(Point(1)(2))(0)) + " " + bob2.name + " " + Ashes.Text.fromInt(bob2.age) + " " + Ashes.Text.fromInt(area(Rect(3)(4)) + area(Circle(2))) + " " + Ashes.Text.fromInt(list.length(pairs)) + " " + Ashes.Text.fromInt(sumFirst(pairs)(0)) + " " + describe(wrapped)
)
