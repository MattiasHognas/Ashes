import Resp as resp
import Ashes.Bytes as bytes
import Ashes.Text as text
import Ashes.UInt as uint
let lowerCode c =
    (let code =
        0
        |> bytes.get(bytes.fromText(c))
        |> uint.toInt
    in
        if code >= 65
        then
            if code <= 90
            then code + 32
            else code
        else code)

let recursive sameWord a b =
    match text.uncons(a) with
        | None ->
            match text.uncons(b) with
                | None -> true
                | Some(_pair) -> false
        | Some((ca, ta)) ->
            match text.uncons(b) with
                | None -> false
                | Some((cb, tb)) ->
                    if lowerCode(ca) == lowerCode(cb)
                    then sameWord(ta)(tb)
                    else false

let emptyStore _ = []

let recursive getValue key pairs =
    match pairs with
        | [] -> None
        | (k, v) :: rest ->
            if k == key
            then Some(v)
            else getValue(key)(rest)

let recursive setValue key value pairs =
    match pairs with
        | [] -> [(key, value)]
        | (k, v) :: rest ->
            if k == key
            then (key, value) :: rest
            else (k, v) :: setValue(key)(value)(rest)

let recursive removeValue key pairs =
    match pairs with
        | [] -> ([], false)
        | (k, v) :: rest ->
            if k == key
            then (rest, true)
            else
                match removeValue(key)(rest) with
                    | (kept, removed) -> ((k, v) :: kept, removed)

let recursive keysOf pairs =
    match pairs with
        | [] -> []
        | (k, _v) :: rest -> k :: keysOf(rest)

let recursive matchingKeys pattern pairs =
    match pairs with
        | [] -> []
        | (k, _v) :: rest ->
            if k == pattern
            then k :: matchingKeys(pattern)(rest)
            else matchingKeys(pattern)(rest)

let recursive deleteAll keys store count =
    match keys with
        | [] -> (count, store)
        | key :: rest ->
            match removeValue(key)(store) with
                | (kept, removed) ->
                    deleteAll(rest)(kept)(if removed
                    then count + 1
                    else count)

let recursive countExisting keys store count =
    match keys with
        | [] -> count
        | key :: rest ->
            countExisting(rest)(store)(match getValue(key)(store) with
                | None -> count
                | Some(_v) -> count + 1)

let wrongArgs name = (resp.errorReply("wrong number of arguments for '" + name + "'"), [], false)

let keepStore store outcome =
    match outcome with
        | (reply, _ignored, quit) -> (reply, store, quit)

let pingCommand store args =
    match args with
        | [] -> (resp.simple("PONG"), store, false)
        | msg :: [] -> (resp.bulk(msg), store, false)
        | _more ->
            "ping"
            |> wrongArgs
            |> keepStore(store)

let echoCommand store args =
    match args with
        | msg :: [] -> (resp.bulk(msg), store, false)
        | _other ->
            "echo"
            |> wrongArgs
            |> keepStore(store)

let setCommand store args =
    match args with
        | key :: value :: [] -> (resp.simple("OK"), setValue(key)(value)(store), false)
        | _other ->
            "set"
            |> wrongArgs
            |> keepStore(store)

let getCommand store args =
    match args with
        | key :: [] ->
            match getValue(key)(store) with
                | None -> (resp.nullBulk(Unit), store, false)
                | Some(value) -> (resp.bulk(value), store, false)
        | _other ->
            "get"
            |> wrongArgs
            |> keepStore(store)

let delCommand store args =
    match args with
        | [] ->
            "del"
            |> wrongArgs
            |> keepStore(store)
        | _keys ->
            match deleteAll(args)(store)(0) with
                | (count, kept) -> (resp.number(count), kept, false)

let existsCommand store args =
    match args with
        | [] ->
            "exists"
            |> wrongArgs
            |> keepStore(store)
        | _keys ->
            (0
            |> countExisting(args)(store)
            |> resp.number, store, false)

let keysCommand store args =
    match args with
        | pattern :: [] ->
            if pattern == "*"
            then
                (store
                |> keysOf
                |> resp.array, store, false)
            else
                (store
                |> matchingKeys(pattern)
                |> resp.array, store, false)
        | _other ->
            "keys"
            |> wrongArgs
            |> keepStore(store)

let commandStub store _args = (resp.array([]), store, false)

let quitCommand store _args = (resp.simple("OK"), store, true)

let attempt name handler pending =
    match pending with
        | (cmd, store, args, Some(outcome)) -> (cmd, store, args, Some(outcome))
        | (cmd, store, args, None) ->
            if sameWord(cmd)(name)
            then
                (cmd, store, args, args
                |> handler(store)
                |> Some)
            else (cmd, store, args, None)

let settle pending =
    match pending with
        | (_cmd, _store, _args, Some(outcome)) -> outcome
        | (cmd, store, _args, None) -> (resp.errorReply("unknown command '" + cmd + "'"), store, false)

let execute store words =
    match words with
        | [] -> (resp.errorReply("empty command"), store, false)
        | cmd :: args ->
            (cmd, store, args, None)
            |> attempt("ping")(pingCommand)
            |> attempt("echo")(echoCommand)
            |> attempt("set")(setCommand)
            |> attempt("get")(getCommand)
            |> attempt("del")(delCommand)
            |> attempt("exists")(existsCommand)
            |> attempt("keys")(keysCommand)
            |> attempt("command")(commandStub)
            |> attempt("quit")(quitCommand)
            |> settle
