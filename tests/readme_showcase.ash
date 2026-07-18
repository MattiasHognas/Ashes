// expect: Price: 12.50, Count: 6
// fmt-skip: kept verbatim in sync with the README hero; the formatter would reshape let! and the pipelines.
// A tiny order-pricing pipeline.
// Pure, immutable, strictly typed — iteration is recursion + match, never loops.
import Ashes.IO as io
import Ashes.Collection.List as list
import Ashes.Text as text
import Ashes.Number.Math as math
import Ashes.Task as async
// An algebraic data type: the drinks on the menu...
// let! awaits a task with no async() wrapper; async.all joins the two tasks
// into one. |?> maps the Ok branch, |!> tags the Error branch.
type Drink =
    | Espresso
    | Latte
    | Drip

// ...and a record: one line on the order.
type Line =
    | drink: Drink
    | qty: Int

// A generic capability with a static provider — like a typeclass instance,
// resolved at compile time with no handler. Prices are floats.
capability Priced(a) =
    | cost : a -> Float

provide Priced(Drink) =
    | cost =
        given (d) ->
            match d with
                | Espresso -> 2.50
                | Latte -> 3.00
                | Drip -> 1.50

let add a b = a + b

let lineCount line =
    match line with
        | Line(_, qty) -> qty

// Recursion + list pattern matching; the provider gives each drink's price,
// widened against the Int quantity with Math.toFloat.
let recursive priceAll lines acc =
    match lines with
        | [] -> acc
        | Line(drink, qty) :: rest -> priceAll rest(Priced.cost(drink) * math.toFloat(qty) + acc)

// Symbol pipeline: the number of drinks on the order.
let count lines =
    lines
    |> list.map lineCount
    |> list.foldLeft add 0

let priceLabel order =
    text.formatFloat(priceAll order 0.0)(2)

let countLabel order =
    order
    |> count
    |> text.fromInt

let fail e = "order failed: " + e

let render labels =
    match labels with
        | price :: drinks :: [] -> "Price: " + price + ", Count: " + drinks
        | _ -> "unexpected"

let merge result =
    match result with
        | Ok(line) -> line
        | Error(line) -> line

let order = [Line(drink = Espresso, qty = 2), Line(drink = Latte, qty = 1), Line(drink = Drip, qty = 3)]

let purchase =
    await async.all [order
    |> priceLabel
    |> async.task, order
    |> countLabel
    |> async.task]
in
    purchase
    |?> render
    |!> fail
    |> merge
    |> io.print
