// expect: resource_close|libresource.so

type Symbol =
    | symbolName: Str
    | libraryName: Str

type Function =
    | name: Str
    | symbol: Symbol
    | destructorForResource: Maybe(Str)

let makeSymbol symbolName libraryName = Symbol(symbolName = symbolName, libraryName = libraryName)

let makeFunction name symbol = Function(name = name, symbol = symbol, destructorForResource = None)

let recursive mark destructorName functions =
    match functions with
        | [] -> []
        | (Function { name = name } as function) :: tail ->
            let updated =
                if name == destructorName
                then function with destructorForResource = Some("Resource")
                else function
            in updated :: mark(destructorName)(tail)

let recursive find name functions =
    match functions with
        | (Function { name = candidate } as function) :: tail ->
            if name == candidate
            then function
            else find(name)(tail)
        | [] ->
            "missing"
            |> makeSymbol("missing")
            |> makeFunction("missing")

let closeResource =
    "libresource.so"
    |> makeSymbol("resource_close")
    |> makeFunction("closeResource")

let disposeText =
    "libtext.so"
    |> makeSymbol("text_dispose")
    |> makeFunction("disposeText")

let inspect =
    "libapi.so"
    |> makeSymbol("inspect_native")
    |> makeFunction("inspect")

let functions =
    [
        closeResource,
        disposeText,
        inspect
    ]

let printSymbol function =
    match function with
        | Function { symbol = symbol } ->
            match symbol with
                | Symbol { symbolName = name, libraryName = library } -> Ashes.IO.print(name + "|" + library)

functions
|> mark("closeResource")
|> find("closeResource")
|> printSymbol
