// The shipped generic Ashes.Collection.List.map and List.filter are element-specialized at a call whose
// callback fixes the element type, so their tail-modulo-constructor spine is built in place from
// reference-counted cells. The hazards that sank an arena spine all concern a head the finished list
// shares with something the loop releases: a record field projected straight off the list being
// consumed, a callback that hands back its own argument, and a captured string appended to every
// element. Each result here is read after the list it was built from is gone, read twice, and fed into
// a second specialized call, so a head the cell failed to retain would be read after release.
// expect: ann,bob,cy|ann,bob,cy|x!,y!,z!|b,d|3:ann,2:bob,1:cy|7,8,9|1,2|[1],[2,1]|42
import Ashes.Collection.List as list
import Ashes.IO as io
import Ashes.Text as text
type Person =
    | name: Str
    | age: Int

let nameOf (person: Person) = person.name

let same (value: Str) = value

let nonEmpty (value: Str) = value != ""

let describe (person: Person) = text.fromInt(person.age) + ":" + person.name

let older (person: Person) = Person(name = person.name, age = person.age + 6)

let ageOf (person: Person) = person.age

let isSmall (value: Int) = value < 3

let wrap (value: Int) = value :: []

let prependTwo (values: List(Int)) = 2 :: values

let recursive people count acc =
    if count == 0
    then acc
    else
        people(count - 1)(Person(name = text.fromInt(count), age = count) :: acc)

let render parts = text.join(",")(parts)

let names =
    Person(name = "ann", age = 3) :: Person(name = "bob", age = 2) :: Person(name = "cy", age = 1) :: []
    |> list.map(nameOf)

let echoed =
    names
    |> list.map(same)

let suffix = text.fromInt(0)
    |> (given (zero) -> if zero == "0" then "!" else "?")

let shouted =
    "x" :: "y" :: "z" :: []
    |> list.map(given (value: Str) -> value + suffix)

let kept =
    "" :: "b" :: "" :: "" :: "d" :: "" :: []
    |> list.filter(nonEmpty)

let described =
    Person(name = "ann", age = 3) :: Person(name = "bob", age = 2) :: Person(name = "cy", age = 1) :: []
    |> list.map(describe)

let aged =
    Person(name = "p", age = 1) :: Person(name = "q", age = 2) :: Person(name = "r", age = 3) :: []
    |> list.map(older)
    |> list.map(ageOf)

let small =
    []
    |> people(5)
    |> list.map(ageOf)
    |> list.filter(isSmall)

let nested =
    1 :: 2 :: []
    |> list.map(wrap)
    |> list.map(given (values: List(Int)) ->
        match values with
            | 2 :: [] -> prependTwo(1 :: [])
            | other -> other)

let showInt (value: Int) = text.fromInt(value)

let renderInts values = values |> list.map(showInt) |> render

let renderNested values =
    values
    |> list.map(given (inner: List(Int)) -> "[" + renderInts(inner) + "]")
    |> render

let total =
    list.foldLeft(given (acc: Int) -> given (value: Int) -> acc + value)(0)(aged) + list.foldLeft(given (acc: Int) -> given (value: Int) -> acc + value)(0)(aged) + list.length(names) - list.length(echoed) + 0 - 6 + 0

render(names) + "|" + render(echoed) + "|" + render(shouted) + "|" + render(kept) + "|" + render(described) + "|" + renderInts(aged) + "|" + renderInts(small) + "|" + renderNested(nested) + "|" + text.fromInt(total) |> io.print
