// Generic tuple updates must preserve both the replacement's polymorphism and
// the known bytes' ownership, including an alias and a source used after call.
type Node =
    | Empty
    | Node(Str, List(Node))

type alias State = (List(Node), Bytes, Bytes, Bool)

let replaceFlag (state: State) flag =
    match state with
        | (nodes, source, alias, _oldFlag) -> (nodes, source, alias, flag)

let apply function argument = function(argument)

let makeState n =
    (let text = "source-" + Ashes.Text.fromInt(n)
    in
        let source = Ashes.Byte.fromText(text)
        in ([Node(text)([])], source, source, false))

let escape n =
    replaceFlag(makeState(n))(true)

let checkBytes source alias expected =
    Ashes.Byte.length(source) == Ashes.Text.byteLength(expected) && Ashes.Byte.length(alias) == Ashes.Text.byteLength(expected) && Ashes.Byte.compare(source)(Ashes.Byte.fromText(expected)) == 0 && Ashes.Byte.compare(alias)(Ashes.Byte.fromText(expected)) == 0

let checkNodes nodes expected =
    match nodes with
        | Node(text, []) :: [] -> text == expected
        | _ -> false

let oneRound n =
    (let expected = "source-" + Ashes.Text.fromInt(n)
    in
        let source = makeState(n)
        in
            let changed = replaceFlag(source)(true)
            in
                let otherType = replaceFlag(source)(expected)
                in
                    let indirect =
                        apply(replaceFlag(source))(false)
                    in
                        let escaped = escape(n)
                        in
                            match (source, changed, otherType, indirect, escaped) with
                                | ((oldNodes, oldBytes, oldAlias, false), (nodes, bytes, alias, true), (textNodes, textBytes, textAlias, text), (indirectNodes, indirectBytes, indirectAlias, false), (escapedNodes, escapedBytes, escapedAlias, true)) -> checkNodes(oldNodes)(expected) && checkBytes(oldBytes)(oldAlias)(expected) && checkNodes(nodes)(expected) && checkBytes(bytes)(alias)(expected) && checkNodes(textNodes)(expected) && checkBytes(textBytes)(textAlias)(expected) && text == expected && checkNodes(indirectNodes)(expected) && checkBytes(indirectBytes)(indirectAlias)(expected) && checkNodes(escapedNodes)(expected) && checkBytes(escapedBytes)(escapedAlias)(expected)
                                | _ -> false)

let ownedParts flag n =
    match makeState(n) with
        | (nodes, source, alias, _oldFlag) -> (nodes, source, alias, flag)

let checkOwned n =
    match ownedParts(true)(n) with
        | (nodes, source, alias, true) ->
            let expected = "source-" + Ashes.Text.fromInt(n)
            in checkNodes(nodes)(expected) && checkBytes(source)(alias)(expected)
        | _ -> false

type alias TextState = (List(Node), Str, Bool)

let replaceTextFlag (state: TextState) flag =
    match state with
        | (nodes, text, _oldFlag) -> (nodes, text, flag)

let checkText n =
    (let text = "text-" + Ashes.Text.fromInt(n)
    in
        let state = ([Node(text)([])], text, false)
        in
            match replaceTextFlag(state)(true) with
                | (nodes, returned, true) -> returned == text && checkNodes(nodes)(text)
                | _ -> false)

let nestedScope flag n =
    ([Node("source-" + Ashes.Text.fromInt(n))([])], let owner = makeState(n)
    in
        match owner with
            | (_nodes, source, _alias, _oldFlag) -> source, flag)

let checkNested n =
    match nestedScope(true)(n) with
        | (nodes, bytes, true) ->
            let expected = "source-" + Ashes.Text.fromInt(n)
            in checkNodes(nodes)(expected) && checkBytes(bytes)(bytes)(expected)
        | _ -> false

let recursive rounds count =
    if count == 0
    then Ashes.IO.print("tuple byte parts passed")
    else
        if oneRound(count) && checkOwned(count) && checkText(count) && checkNested(count)
        then rounds(count - 1)
        else Ashes.IO.panic("tuple byte parts failed")

match Ashes.IO.args with
    | "1" :: [] -> rounds(1)
    | "1000" :: [] -> rounds(1000)
    | "10000" :: [] -> rounds(10000)
    | _ -> Ashes.IO.panic("count")
