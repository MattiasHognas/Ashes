import Ashes.Net.Http.Server as http
import Ashes.Text.Json as json
import Ashes.Collection.List as list
import Ashes.Text as str
import Ashes.Text as text
capability Store =
    | load : Unit -> Str
    | save : Str -> Unit

let recursive escapeJson acc input =
    match text.uncons(input) with
        | None -> acc
        | Some((h, t)) ->
            if h == "\""
            then escapeJson(acc + "\\\"")(t)
            else
                if h == "\\"
                then escapeJson(acc + "\\\\")(t)
                else escapeJson(acc + h)(t)

let renderTodo todo =
    match todo with
        | (id, title, done) ->
            "{\"id\":" + text.fromInt(id) + ",\"title\":\"" + escapeJson("")(title) + "\",\"done\":" + (if done
            then "true"
            else "false") + "}"

let renderTodos todos =
    "[" + str.join(",")(list.map(renderTodo)(todos)) + "]"

let readId elem =
    elem
    |> json.get("id")
    |?> json.asInt
    |?> (given (id) -> (elem, id))

let readTitle state =
    match state with
        | (elem, id) ->
            elem
            |> json.get("title")
            |?> json.asStr
            |?> (given (title) -> (elem, id, title))

let readDone state =
    match state with
        | (elem, id, title) ->
            elem
            |> json.get("done")
            |?> json.asBool
            |?> (given (done) -> (id, title, done))

let todoFields elem =
    elem
    |> readId
    |?> readTitle
    |?> readDone

let recursive reverseTodos acc todos =
    match todos with
        | [] -> acc
        | todo :: rest -> reverseTodos(todo :: acc)(rest)

let recursive collectTodos doc i acc =
    match json.index(i)(doc) with
        | Error(_pastEnd) -> reverseTodos([])(acc)
        | Ok(elem) ->
            match todoFields(elem) with
                | Error(_malformed) -> collectTodos(doc)(i + 1)(acc)
                | Ok(todo) -> collectTodos(doc)(i + 1)(todo :: acc)

let parseTodos raw =
    match json.parse(raw) with
        | Error(_notJson) -> []
        | Ok(doc) -> collectTodos(doc)(0)([])

let loadTodos _ =
    Unit
    |> Store.load
    |> parseTodos

let saveTodos todos =
    todos
    |> renderTodos
    |> Store.save

let recursive findTodo id todos =
    match todos with
        | [] -> None
        | (tid, title, done) :: rest ->
            if tid == id
            then Some((tid, title, done))
            else findTodo(id)(rest)

let recursive maxId todos acc =
    match todos with
        | [] -> acc
        | (tid, _title, _done) :: rest ->
            maxId(rest)(if tid > acc
            then tid
            else acc)

let nextId todos = maxId(todos)(0) + 1

let recursive removeTodo id todos =
    match todos with
        | [] -> ([], false)
        | (tid, title, done) :: rest ->
            if tid == id
            then (rest, true)
            else
                match removeTodo(id)(rest) with
                    | (kept, removed) -> ((tid, title, done) :: kept, removed)

let recursive replaceTodo id title done todos =
    match todos with
        | [] -> ([], false)
        | (tid, oldTitle, oldDone) :: rest ->
            if tid == id
            then ((tid, title, done) :: rest, true)
            else
                match replaceTodo(id)(title)(done)(rest) with
                    | (kept, replaced) -> ((tid, oldTitle, oldDone) :: kept, replaced)

let titleOf bodyText =
    match json.parse(bodyText) with
        | Error(_notJson) -> Error("body must be a JSON object")
        | Ok(parsed) ->
            match json.get("title")(parsed) with
                | Error(_missing) -> Error("missing field: title")
                | Ok(titleJson) ->
                    match json.asStr(titleJson) with
                        | Error(_notStr) -> Error("title must be a string")
                        | Ok(title) -> Ok(title)

let doneOf bodyText =
    match json.parse(bodyText) with
        | Error(_notJson) -> false
        | Ok(parsed) ->
            match json.get("done")(parsed) with
                | Error(_missing) -> false
                | Ok(doneJson) ->
                    match json.asBool(doneJson) with
                        | Error(_notBool) -> false
                        | Ok(done) -> done

let errorBody msg = "{\"error\":\"" + escapeJson("")(msg) + "\"}"

let notFound _ =
    "not found"
    |> errorBody
    |> http.json(404)

let badRequest msg =
    msg
    |> errorBody
    |> http.json(400)

let methodNotAllowed _ =
    "method not allowed"
    |> errorBody
    |> http.json(405)

let noContent _ = http.respond(204)("")("")

let collectionRoute m bodyText =
    if m == "GET"
    then
        Unit
        |> loadTodos
        |> renderTodos
        |> http.json(200)
    else
        if m == "POST"
        then
            match titleOf(bodyText) with
                | Error(msg) -> badRequest(msg)
                | Ok(title) ->
                    let todos = loadTodos(Unit)
                    in
                        let todo = (nextId(todos), title, doneOf(bodyText))
                        in
                            let _saved =
                                [todo]
                                |> list.append(todos)
                                |> saveTodos
                            in
                                todo
                                |> renderTodo
                                |> http.json(201)
        else methodNotAllowed(Unit)

let itemRoute m idText bodyText =
    match text.parseInt(idText) with
        | Error(_notInt) -> badRequest("invalid id")
        | Ok(id) ->
            if m == "GET"
            then
                match Unit
                |> loadTodos
                |> findTodo(id) with
                    | None -> notFound(Unit)
                    | Some(todo) ->
                        todo
                        |> renderTodo
                        |> http.json(200)
            else
                if m == "PUT"
                then
                    match titleOf(bodyText) with
                        | Error(msg) -> badRequest(msg)
                        | Ok(title) ->
                            let done = doneOf(bodyText)
                            in
                                match Unit
                                |> loadTodos
                                |> replaceTodo(id)(title)(done) with
                                    | (kept, replaced) ->
                                        if replaced
                                        then
                                            let _saved = saveTodos(kept)
                                            in
                                                (id, title, done)
                                                |> renderTodo
                                                |> http.json(200)
                                        else notFound(Unit)
                else
                    if m == "DELETE"
                    then
                        match Unit
                        |> loadTodos
                        |> removeTodo(id) with
                            | (kept, removed) ->
                                if removed
                                then
                                    let _saved = saveTodos(kept)
                                    in noContent(Unit)
                                else notFound(Unit)
                    else methodNotAllowed(Unit)

let recursive dropEmpty segs =
    match segs with
        | [] -> []
        | s :: rest ->
            if s == ""
            then dropEmpty(rest)
            else s :: dropEmpty(rest)

let route req =
    (let m = http.method(req)
    in
        let bodyText = http.body(req)
        in
            match "/"
            |> str.split(http.path(req))
            |> dropEmpty with
                | seg :: [] ->
                    if seg == "todos"
                    then collectionRoute(m)(bodyText)
                    else notFound(Unit)
                | seg :: idText :: [] ->
                    if seg == "todos"
                    then itemRoute(m)(idText)(bodyText)
                    else notFound(Unit)
                | _other -> notFound(Unit))
