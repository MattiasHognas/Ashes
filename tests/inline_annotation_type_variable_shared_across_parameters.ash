// expect: 200

// A generic helper whose curried parameters are annotated with a type variable (`List(a)`): the
// name is one flexible variable across the parameters, so the helper is generic over its
// element and its loop is placed like any generic loop.
let recursive reverseOnto (items: List(a)) (acc: List(a)) =
    match items with
        | [] -> acc
        | head :: rest -> reverseOnto(rest)(head :: acc)

let reverse (items: List(a)) = reverseOnto(items)([])

type Binding =
    | name: Str
    | index: Int

let recursive pruneCaptureList captures (index: Int) survivors remap =
    match captures with
        | [] -> (reverse(survivors), reverse(remap))
        | capture :: rest ->
            if index % 2 == 0
            then pruneCaptureList(rest)(index + 1)(capture :: survivors)((index, index) :: remap)
            else pruneCaptureList(rest)(index + 1)(survivors)(remap)

let recursive count items =
    match items with
        | [] -> 0
        | _ :: rest -> 1 + count(rest)

let captures = [Binding(name = "a", index = 0), Binding(name = "b", index = 1), Binding(name = "c", index = 2)]

let recursive rounds (n: Int) (total: Int) =
    if n == 0
    then total
    else
        match pruneCaptureList(captures)(0)([])([]) with
            | (survivors, remap) -> rounds(n - 1)(total + count(survivors) + count(remap))

0
|> rounds(50)
|> Ashes.IO.print
