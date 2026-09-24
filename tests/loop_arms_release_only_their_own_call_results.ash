// A loop whose match arms each hold a record a call returned owned before jumping back: every back
// edge releases the results its own arm stored, and none an arm the same iteration never ran.
// expect: 763120
type Fact =
    | name: Str
    | value: Int

let fact (name: Str) (value: Int) = Fact(name = name, value = value)

let weigh (item: Fact) = Ashes.Text.byteLength(item.name) + item.value

let recursive fold (step: Int) (limit: Int) (facts: List(Fact)) (total: Int) =
    if step >= limit
    then total + Ashes.Collection.List.length(facts)
    else
        match step % 3 with
            | 0 ->
                let made = fact("zero" + Ashes.Text.fromInt(step))(step)
                in fold(step + 1)(limit)(facts)(total + weigh(made))
            | 1 ->
                let made = fact("one")(step * 2)
                in fold(step + 1)(limit)(made :: facts)(total + weigh(made))
            | _ ->
                match facts with
                    | [] -> fold(step + 1)(limit)(facts)(total)
                    | first :: rest ->
                        fold(step + 1)(limit)(rest)(total + weigh(first) + weigh(fact("two")(1)))

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else rounds(n - 1)(total + fold(0)(300)([])(0))

0
|> rounds(10)
|> Ashes.IO.print
