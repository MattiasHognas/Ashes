// expect: inspect_native|libapi.so

import Ashes.Collection.List.reverse
import Ashes.Text as Text
let chooseExternalName functionName written =
    match written with
        | None -> functionName
        | Some(value) -> value

let rebuildExternalSymbol fullName parts =
    match reverse(parts) with
        | [] -> (fullName, None)
        | _ :: [] -> (fullName, None)
        | library :: reversedSymbol ->
            let symbol =
                reversedSymbol
                |> reverse
                |> Text.join("@")
            in
                if library == ""
                then (symbol, None)
                else (symbol, Some(library))

let splitExternalSymbol functionName written =
    written
    |> chooseExternalName(functionName)
    |> (given (fullName) ->
        "@"
        |> Text.split(fullName)
        |> rebuildExternalSymbol(fullName))

let first = splitExternalSymbol("inspect")(Some("inspect_native@libapi.so"))

let second = splitExternalSymbol("dispose")(Some("text_dispose@libtext.so"))

match (first, second) with
    | ((symbol, Some(library)), _) -> Ashes.IO.print(symbol + "|" + library)
    | _ -> Ashes.IO.print("missing")
