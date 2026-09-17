// expect: one at src/b | M.alpha.ash at lib/root/alpha.ash; M.gamma.ash at lib/root/gamma.ash; M.delta.ash at lib/root/delta.ash;
type Source =
    | name: Str
    | path: Str

type Item =
    | moduleName: Str
    | sourcePath: Str
    | source: Str

let recursive pathsFor (name: Str) (sources: List(Source)) =
    match sources with
        | [] -> []
        | Source { name = candidate, path = path } :: rest ->
            if candidate == name
            then path + "" :: pathsFor(name)(rest)
            else pathsFor(name)(rest)

// Binds `path` out of a call result, which makes the name an alias of that result's owner.
let pick (name: Str) (sources: List(Source)) =
    match pathsFor(name)(sources) with
        | [] -> "none"
        | path :: [] -> "one at " + path
        | _ -> "many"

let readSource (path: Str) =
    if Ashes.Text.length(path) > 100
    then Error("path too long")
    else Ok("source of " + path)

// A different function's own `path`: a call result stored in a record that rides the back edge,
// while the binding itself is released there. The record has to hold a reference of its own.
let recursive load root names loaded =
    match names with
        | [] ->
            loaded
            |> Ashes.Collection.List.reverse
            |> Ok
        | name :: rest ->
            if Ashes.Text.length(name) < 5
            then load(root)(rest)(loaded)
            else
                let path = Ashes.IO.Path.join(Ashes.IO.Path.Unix)(root)(name)
                in
                    match readSource(path) with
                        | Error(message) -> Error("could not read " + path + ": " + message)
                        | Ok(source) -> load(root)(rest)(Item(moduleName = "M." + name, sourcePath = path, source = source) :: loaded)

let recursive describe (items: List(Item)) (acc: Str) =
    match items with
        | [] -> acc
        | Item { moduleName = name, sourcePath = path } :: rest -> describe(rest)(acc + " " + name + " at " + path + ";")

let picked = pick("b")([Source(name = "a", path = "src/a"), Source(name = "b", path = "src/b")])

match load("lib/root")(["alpha.ash", "b", "gamma.ash", "delta.ash"])([]) with
    | Error(message) -> Ashes.IO.print(message)
    | Ok(items) -> Ashes.IO.print(picked + " |" + describe(items)(""))
