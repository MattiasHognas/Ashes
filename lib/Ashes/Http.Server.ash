// Ashes.Http.Server — a minimal HTTP/1.1 server layered over Ashes.Net.Tcp.Server. The handler maps
// a request to a response directly (handler : HttpRequest -> HttpResponse); `serve` reads one request
// per connection, parses the request line, runs the handler, writes the response, and closes
// (Connection: close). Pure Ashes over the TCP layer, so it works on every target the TCP server does.
// This is intentionally small (GET-style request line + a single receive); streaming bodies,
// keep-alive, and async handlers are future work — see docs/future/SERVER_SUPPORT.md.
import Ashes.Net.Tcp
import Ashes.Net.Tcp.Server
import Ashes.String
import Ashes.Text
import Ashes.Async
type HttpRequest =
    | HttpRequest(Str, Str)

type HttpResponse =
    | HttpResponse(Int, Str)

let method req = 
    match req with
        | HttpRequest(m, _p) -> m

let path req = 
    match req with
        | HttpRequest(_m, p) -> p

let text status bodyText = HttpResponse(status)(bodyText)

let parseRequest raw = 
    match Ashes.String.split(raw)("\r\n") with
        | [] -> HttpRequest("GET")("/")
        | line :: _rest -> 
            match Ashes.String.split(line)(" ") with
                | m :: p :: _v -> HttpRequest(m)(p)
                | _other -> HttpRequest("GET")("/")

let render resp = 
    match resp with
        | HttpResponse(status, bodyText) -> 
            let statusReason = 
                if status == 200
                then "OK"
                else 
                    if status == 201
                    then "Created"
                    else 
                        if status == 400
                        then "Bad Request"
                        else 
                            if status == 404
                            then "Not Found"
                            else 
                                if status == 500
                                then "Internal Server Error"
                                else "OK"
            in "HTTP/1.1 " + Ashes.Text.fromInt(status) + " " + statusReason + "\r\nContent-Length: " + Ashes.Text.fromInt(Ashes.String.length(bodyText)) + "\r\nConnection: close\r\n\r\n" + bodyText

let serve port handler = 
    Ashes.Net.Tcp.Server.serve(port)(given (client) -> 
        async(match await Ashes.Net.Tcp.receive(client)(65536) with
            | Error(e) -> Error(e)
            | Ok(raw) -> 
                let resp = handler(parseRequest(raw))
                in 
                    match await Ashes.Net.Tcp.send(client)(render(resp)) with
                        | Error(e2) -> Error(e2)
                        | Ok(_n) -> await Ashes.Net.Tcp.close(client)))
