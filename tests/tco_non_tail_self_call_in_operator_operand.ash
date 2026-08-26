// expect: 2 2 2 4 1 1
import Ashes.IO
type Item =
    | Field(Int, Int, Int)
    | Other(Int)

type Wrapped =
    | inner: Item
    | note: Maybe(Str)

let recursive countLeft n =
    if n == 0
    then 0
    else
        if n % 2 == 0
        then 1 + countLeft(n - 1)
        else countLeft(n - 1)

let recursive countRight n =
    if n == 0
    then 0
    else
        if n % 2 == 0
        then countRight(n - 1) + 1
        else countRight(n - 1)

let recursive countEvens xs =
    match xs with
        | [] -> 0
        | x :: tail ->
            if x % 2 == 0
            then 1 + countEvens(tail)
            else countEvens(tail)

let recursive sumTo n =
    if n == 0
    then 0
    else n + sumTo(n - 1)

let recursive countFields items =
    match items with
        | [] -> 0
        | Wrapped { inner = Field(_, _, _) } :: tail -> 1 + countFields(tail)
        | _ :: tail -> countFields(tail)

let recursive countNegated items =
    match items with
        | [] -> 0
        | Wrapped { inner = Field(_, _, _) } :: tail ->
            if !(countNegated(tail) == 1)
            then 1
            else 0
        | _ :: tail -> countNegated(tail)

let items = [Wrapped(inner = Other(0), note = None), Wrapped(inner = Field(1)(0)(0), note = None), Wrapped(inner = Other(3), note = None), Wrapped(inner = Other(5), note = None)]

print(Ashes.Trait.Show.show(countLeft(4)) + " " + Ashes.Trait.Show.show(countRight(4)) + " " + Ashes.Trait.Show.show(countEvens([1, 2, 3, 4])) + " " + Ashes.Trait.Show.show(sumTo(1) + sumTo(2)) + " " + Ashes.Trait.Show.show(countFields(items)) + " " + Ashes.Trait.Show.show(countNegated(items)))
