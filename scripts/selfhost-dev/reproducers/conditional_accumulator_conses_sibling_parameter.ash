let recursive containsName (name: Str) (names: List(Str)) =
    match names with
        | [] -> false
        | head :: tail ->
            if head == name
            then true
            else containsName(name)(tail)

let targetsOf (width: Int) (name: Str) =
    if Ashes.Text.byteLength(name) < width
    then [name + "a", name + "b"]
    else []

let recursive computeSccs (order: List(Str)) (stack: List(List(Str))) (width: Int) (visited: List(Str)) (component: List(Str)) (components: List(List(Str))) =
    match stack with
        | [] :: rest -> computeSccs(order)(rest)(width)(visited)(component)(components)
        | (name :: more) :: rest ->
            if containsName(name)(visited)
            then computeSccs(order)(more :: rest)(width)(visited)(component)(components)
            else computeSccs(order)(targetsOf(width)(name) :: more :: rest)(width)(name :: visited)(name :: component)(components)
        | [] ->
            let finished =
                match component with
                    | [] -> components
                    | _ -> component :: components
            in
                match order with
                    | [] -> finished
                    | head :: tail ->
                        if containsName(head)(visited)
                        then computeSccs(tail)([])(width)(visited)([])(finished)
                        else computeSccs(tail)([[head]])(width)(visited)([])(finished)

let recursive weighOne (xs: List(Str)) (acc: Int) =
    match xs with
        | [] -> acc
        | name :: rest -> weighOne(rest)(acc + Ashes.Text.byteLength(name))

let recursive weigh (xs: List(List(Str))) (acc: Int) =
    match xs with
        | [] -> acc
        | component :: rest -> weigh(rest)(weighOne(component)(acc) + 1)

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        rounds(n - 1)(total + weigh(computeSccs(["x", "y", "x", "zz", "w"])([])(4)([])([])([]))(0))

0
|> rounds(ROUNDS)
|> Ashes.IO.print
