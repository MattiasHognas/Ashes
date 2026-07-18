import Kv as kv
import Resp as resp
import Ashes.Async as tasks
import Ashes.IO as io
import Ashes.Net.Tcp as tcp
import Ashes.Net.Tcp.Server as server
let port = 6380

let _banner = io.writeLine("key-value store listening on 127.0.0.1:6380")

match tasks.run(async(match await server.listen(port) with
    | Error(bindError) -> Error(bindError)
    | Ok(listener) ->
        let recursive connLoop client buffer store =
            match resp.parse(buffer) with
                | RespMalformed(msg) ->
                    let _sent = await tcp.send(client)(resp.errorReply(msg))
                    in
                        let _closed = await tcp.close(client)
                        in store
                | RespParsed(words, rest) ->
                    match kv.execute(store)(words) with
                        | (reply, newStore, quit) ->
                            match await tcp.send(client)(reply) with
                                | Error(_sendError) ->
                                    let _closed2 = await tcp.close(client)
                                    in newStore
                                | Ok(_sentBytes) ->
                                    if quit
                                    then
                                        let _closed3 = await tcp.close(client)
                                        in newStore
                                    else connLoop(client)(rest)(newStore)
                | RespNeedMore ->
                    match await tcp.receive(client)(65536) with
                        | Error(_recvError) ->
                            let _closed4 = await tcp.close(client)
                            in store
                        | Ok(chunk) ->
                            if chunk == ""
                            then
                                let _closed5 = await tcp.close(client)
                                in store
                            else connLoop(client)(buffer + chunk)(store)
        in
            let recursive acceptLoop store =
                match await server.accept(listener) with
                    | Error(acceptError) ->
                        if acceptError == "__ashes_server_shutdown"
                        then Ok(Unit)
                        else Error(acceptError)
                    | Ok(client) -> acceptLoop(connLoop(client)("")(store))
            in acceptLoop(kv.emptyStore(Unit)))) with
    | Ok(Ok(_stopped)) -> io.print("server stopped")
    | Ok(Error(loopError)) -> io.print(loopError)
    | Error(runError) -> io.print(runError)
