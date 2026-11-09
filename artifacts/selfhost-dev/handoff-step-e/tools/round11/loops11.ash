type Item =
    | name: Str
    | weight: Int

let recursive walk1 (items: List(Item)) (seen: List(Str)) (total: Int) =
    match items with
        | [] -> total + Ashes.Collection.List.length(seen)
        | item :: rest ->
            let next =
                if item.weight > 1
                then item.name :: seen
                else seen
            in walk1(rest)(next)(total + item.weight)

let recursive walk2 (items: List(Item)) (seen: List(Str)) (total: Int) =
    match items with
        | [] -> total + Ashes.Collection.List.length(seen)
        | item :: rest ->
            let next =
                if item.weight > 2
                then item.name :: seen
                else seen
            in walk2(rest)(next)(total + item.weight)

let recursive walk3 (items: List(Item)) (seen: List(Str)) (total: Int) =
    match items with
        | [] -> total + Ashes.Collection.List.length(seen)
        | item :: rest ->
            let next =
                if item.weight > 3
                then item.name :: seen
                else seen
            in walk3(rest)(next)(total + item.weight)

let recursive walk4 (items: List(Item)) (seen: List(Str)) (total: Int) =
    match items with
        | [] -> total + Ashes.Collection.List.length(seen)
        | item :: rest ->
            let next =
                if item.weight > 4
                then item.name :: seen
                else seen
            in walk4(rest)(next)(total + item.weight)

let recursive walk5 (items: List(Item)) (seen: List(Str)) (total: Int) =
    match items with
        | [] -> total + Ashes.Collection.List.length(seen)
        | item :: rest ->
            let next =
                if item.weight > 5
                then item.name :: seen
                else seen
            in walk5(rest)(next)(total + item.weight)

let recursive walk6 (items: List(Item)) (seen: List(Str)) (total: Int) =
    match items with
        | [] -> total + Ashes.Collection.List.length(seen)
        | item :: rest ->
            let next =
                if item.weight > 6
                then item.name :: seen
                else seen
            in walk6(rest)(next)(total + item.weight)

let recursive walk7 (items: List(Item)) (seen: List(Str)) (total: Int) =
    match items with
        | [] -> total + Ashes.Collection.List.length(seen)
        | item :: rest ->
            let next =
                if item.weight > 7
                then item.name :: seen
                else seen
            in walk7(rest)(next)(total + item.weight)

let recursive walk8 (items: List(Item)) (seen: List(Str)) (total: Int) =
    match items with
        | [] -> total + Ashes.Collection.List.length(seen)
        | item :: rest ->
            let next =
                if item.weight > 8
                then item.name :: seen
                else seen
            in walk8(rest)(next)(total + item.weight)

let recursive walk9 (items: List(Item)) (seen: List(Str)) (total: Int) =
    match items with
        | [] -> total + Ashes.Collection.List.length(seen)
        | item :: rest ->
            let next =
                if item.weight > 9
                then item.name :: seen
                else seen
            in walk9(rest)(next)(total + item.weight)

let recursive walk10 (items: List(Item)) (seen: List(Str)) (total: Int) =
    match items with
        | [] -> total + Ashes.Collection.List.length(seen)
        | item :: rest ->
            let next =
                if item.weight > 10
                then item.name :: seen
                else seen
            in walk10(rest)(next)(total + item.weight)

let recursive walk11 (items: List(Item)) (seen: List(Str)) (total: Int) =
    match items with
        | [] -> total + Ashes.Collection.List.length(seen)
        | item :: rest ->
            let next =
                if item.weight > 11
                then item.name :: seen
                else seen
            in walk11(rest)(next)(total + item.weight)

let items = [Item(name = "a", weight = 1), Item(name = "b", weight = 40)]

Ashes.IO.print(0 + walk1(items)([])(0) + walk2(items)([])(0) + walk3(items)([])(0) + walk4(items)([])(0) + walk5(items)([])(0) + walk6(items)([])(0) + walk7(items)([])(0) + walk8(items)([])(0) + walk9(items)([])(0) + walk10(items)([])(0) + walk11(items)([])(0))
