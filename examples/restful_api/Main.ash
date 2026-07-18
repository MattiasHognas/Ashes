import Api
import Ashes.Async as tasks
import Ashes.File as file
import Ashes.Http.Server as http
import Ashes.IO as io
provide Store =
    | load =
        given (_) ->
            match file.readText("todos.json") with
                | Ok(contents) -> contents
                | Error(_missing) -> "[]"
    | save =
        given (contents) ->
            match file.writeText("todos.json")(contents) with
                | Ok(_written) -> Unit
                | Error(e) -> io.panic("cannot write todos.json: " + e)

let onRequest req = async(Api.route(req))

let _banner = io.writeLine("todo API listening on http://127.0.0.1:8080")

match tasks.run(http.serveParallel(8080)(1)(onRequest)) with
    | Ok(_stopped) -> io.print("server stopped")
    | Error(e) -> io.print(e)
