// Ashes.Http.Server — a minimal HTTP/1.1 server layered over Ashes.Net.Tcp.Server. A handler maps a
// request to a response (handler : HttpRequest -> Task(E, HttpResponse), so handlers can `await`
// async work such as a downstream call); `serve` reads one request per connection, parses the request
// line + headers + body, runs the handler, writes the response, and closes (Connection: close). Pure
// Ashes over the TCP layer, so it works on every target the TCP server does. Intentionally small: one
// receive per connection (so the request must fit one read), no keep-alive, no chunked/streaming
// bodies — see docs/md/future/SERVER_SUPPORT.md for what remains.
//
// ADT field types must be simple type names, so a request keeps its headers as the raw header block
// (a Str) and a response keeps its extra headers as a pre-rendered "Name: value\r\n..." block; the
// header accessors and builders below hide that representation.
import Ashes.Net.Tcp
import Ashes.Net.Tcp.Server
import Ashes.String
import Ashes.Text
import Ashes.Bytes
import Ashes.UInt
import Ashes.Async
type HttpRequest =
    | HttpRequest(Str, Str, Str, Str)

type HttpResponse =
    | HttpResponse(Int, Str, Str)

let method req = 
    match req with
        | HttpRequest(m, _p, _h, _b) -> m

let path req = 
    match req with
        | HttpRequest(_m, p, _h, _b) -> p

let rawHeaders req = 
    match req with
        | HttpRequest(_m, _p, h, _b) -> h

let body req = 
    match req with
        | HttpRequest(_m, _p, _h, b) -> b

let charLowerCode c = 
    (let code = Ashes.UInt.toInt(Ashes.Bytes.get(Ashes.Bytes.fromText(c))(0))
    in 
        if code >= 65
        then 
            if code <= 90
            then code + 32
            else code
        else code)

let recursive sameHeaderName a b = 
    match Ashes.Text.uncons(a) with
        | None -> 
            match Ashes.Text.uncons(b) with
                | None -> true
                | Some(_pair) -> false
        | Some((ca, ta)) -> 
            match Ashes.Text.uncons(b) with
                | None -> false
                | Some((cb, tb)) -> 
                    if charLowerCode(ca) == charLowerCode(cb)
                    then sameHeaderName(ta)(tb)
                    else false

let header req name = 
    (let recursive scan lines = 
        match lines with
            | [] -> None
            | line :: rest -> 
                let idx = Ashes.String.indexOf(line)(":")
                in 
                    if idx < 0
                    then scan(rest)
                    else 
                        if sameHeaderName(Ashes.String.trim(Ashes.String.take(line)(idx)))(name)
                        then Some(Ashes.String.trim(Ashes.String.drop(line)(idx + 1)))
                        else scan(rest)
    in scan(Ashes.String.split(rawHeaders(req))("\r\n")))

let respond status headerBlock bodyText = HttpResponse(status)(headerBlock)(bodyText)

let text status bodyText = HttpResponse(status)("Content-Type: text/plain; charset=utf-8\r\n")(bodyText)

let json status bodyText = HttpResponse(status)("Content-Type: application/json\r\n")(bodyText)

let withHeader name value resp = 
    match resp with
        | HttpResponse(status, headerBlock, bodyText) -> HttpResponse(status)(name + ": " + value + "\r\n" + headerBlock)(bodyText)

let parseRequest raw = 
    (let sections = Ashes.String.split(raw)("\r\n\r\n")
    in 
        let headSection = 
            match sections with
                | first :: _rest -> first
                | [] -> ""
        in 
            let bodyText = 
                match sections with
                    | _first :: bodyPart :: _more -> bodyPart
                    | _other -> ""
            in 
                let requestLine = 
                    match Ashes.String.split(headSection)("\r\n") with
                        | first :: _rest -> first
                        | [] -> ""
                in 
                    let idx = Ashes.String.indexOf(headSection)("\r\n")
                    in 
                        let headerBlock = 
                            if idx < 0
                            then ""
                            else Ashes.String.drop(headSection)(idx + 2)
                        in 
                            match Ashes.String.split(requestLine)(" ") with
                                | m :: p :: _v -> HttpRequest(m)(p)(headerBlock)(bodyText)
                                | _other -> HttpRequest("GET")("/")(headerBlock)(bodyText))

let reasonPhrase status = 
    if status == 200
    then "OK"
    else 
        if status == 201
        then "Created"
        else 
            if status == 204
            then "No Content"
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

let render resp = 
    match resp with
        | HttpResponse(status, headerBlock, bodyText) -> "HTTP/1.1 " + Ashes.Text.fromInt(status) + " " + reasonPhrase(status) + "\r\n" + headerBlock + "Content-Length: " + Ashes.Text.fromInt(Ashes.Text.byteLength(bodyText)) + "\r\nConnection: close\r\n\r\n" + bodyText

let serve port handler = 
    Ashes.Net.Tcp.Server.serve(port)(given (client) -> 
        async(match await Ashes.Net.Tcp.receive(client)(65536) with
            | Error(e) -> Error(e)
            | Ok(raw) -> 
                let response = 
                    match await handler(parseRequest(raw)) with
                        | Ok(resp) -> render(resp)
                        | Error(_he) -> render(text(500)("Internal Server Error"))
                in 
                    match await Ashes.Net.Tcp.send(client)(response) with
                        | Error(e2) -> Error(e2)
                        | Ok(_n) -> await Ashes.Net.Tcp.close(client)))
