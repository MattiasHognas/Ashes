// Ashes.Http.Server — an HTTP/1.1 server layered over Ashes.Net.Tcp.Server. A handler maps a request
// to a response (handler : HttpRequest -> Task(E, HttpResponse), so handlers can `await` async work
// such as a downstream call); `serve` reads a request, runs the handler, writes the response, and —
// per HTTP/1.1 — keeps the connection alive (closing on `Connection: close`, on handler failure, or
// when the peer disconnects). Pure Ashes over the TCP layer, so it works on every target the TCP
// server does. Reads are buffered until a full request has arrived (headers complete and
// Content-Length bytes of body), so requests larger than one read and slow/split requests work.
// Chunked transfer-encoding request bodies are not supported — a body must be sized by
// Content-Length. See docs/md/future/SERVER_SUPPORT.md for what remains.
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

type HttpParse =
    | HttpNeedMore
    | HttpParsed(HttpRequest, Bool, Str)

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

let headerInBlock block name = 
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
    in scan(Ashes.String.split(block)("\r\n")))

let header req name = headerInBlock(rawHeaders(req))(name)

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

let renderConnection connectionValue resp = 
    match resp with
        | HttpResponse(status, headerBlock, bodyText) -> "HTTP/1.1 " + Ashes.Text.fromInt(status) + " " + reasonPhrase(status) + "\r\n" + headerBlock + "Content-Length: " + Ashes.Text.fromInt(Ashes.Text.byteLength(bodyText)) + "\r\nConnection: " + connectionValue + "\r\n\r\n" + bodyText

let render resp = renderConnection("close")(resp)

let keepAliveOf headerBlock version = 
    match headerInBlock(headerBlock)("connection") with
        | Some(conn) -> 
            if sameHeaderName(conn)("close")
            then false
            else true
        | None -> 
            if sameHeaderName(version)("HTTP/1.0")
            then false
            else true

let tryParseBuffered buffered = 
    (let headEnd = Ashes.String.indexOf(buffered)("\r\n\r\n")
    in 
        if headEnd < 0
        then HttpNeedMore
        else 
            let headSection = Ashes.String.take(buffered)(headEnd)
            in 
                let firstLineEnd = Ashes.String.indexOf(headSection)("\r\n")
                in 
                    let requestLine = 
                        if firstLineEnd < 0
                        then headSection
                        else Ashes.String.take(headSection)(firstLineEnd)
                    in 
                        let headerBlock = 
                            if firstLineEnd < 0
                            then ""
                            else Ashes.String.drop(headSection)(firstLineEnd + 2)
                        in 
                            let contentLength = 
                                match headerInBlock(headerBlock)("content-length") with
                                    | None -> 0
                                    | Some(lenText) -> 
                                        match Ashes.Text.parseInt(lenText) with
                                            | Error(_pe) -> 0
                                            | Ok(n) -> 
                                                if n < 0
                                                then 0
                                                else n
                            in 
                                let allBytes = Ashes.Bytes.fromText(buffered)
                                in 
                                    let headBytes = headEnd + 4
                                    in 
                                        let availableBody = Ashes.Bytes.length(allBytes) - headBytes
                                        in 
                                            if availableBody < contentLength
                                            then HttpNeedMore
                                            else 
                                                let bodyText = Ashes.Bytes.subText(allBytes)(headBytes)(contentLength)
                                                in 
                                                    let rest = Ashes.Bytes.subText(allBytes)(headBytes + contentLength)(availableBody - contentLength)
                                                    in 
                                                        let version = 
                                                            match Ashes.String.split(requestLine)(" ") with
                                                                | _m :: _p :: v :: _more -> v
                                                                | _other -> "HTTP/1.1"
                                                        in 
                                                            let keepAlive = keepAliveOf(headerBlock)(version)
                                                            in 
                                                                match Ashes.String.split(requestLine)(" ") with
                                                                    | m :: pth :: _v -> HttpParsed(HttpRequest(m)(pth)(headerBlock)(bodyText))(keepAlive)(rest)
                                                                    | _other -> HttpParsed(HttpRequest("GET")("/")(headerBlock)(bodyText))(keepAlive)(rest))

let serve port handler = 
    Ashes.Net.Tcp.Server.serve(port)(given (client) -> 
        async(let recursive connLoop buffered = 
            match tryParseBuffered(buffered) with
                | HttpNeedMore -> 
                    match await Ashes.Net.Tcp.receive(client)(65536) with
                        | Error(e) -> Error(e)
                        | Ok(chunk) -> 
                            if Ashes.Text.byteLength(chunk) == 0
                            then await Ashes.Net.Tcp.close(client)
                            else connLoop(buffered + chunk)
                | HttpParsed(req, keepAlive, rest) -> 
                    match await handler(req) with
                        | Ok(resp) -> 
                            let wire = 
                                renderConnection(if keepAlive
                                then "keep-alive"
                                else "close")(resp)
                            in 
                                match await Ashes.Net.Tcp.send(client)(wire) with
                                    | Error(e2) -> Error(e2)
                                    | Ok(_n) -> 
                                        if keepAlive
                                        then connLoop(rest)
                                        else await Ashes.Net.Tcp.close(client)
                        | Error(_he) -> 
                            match await Ashes.Net.Tcp.send(client)(render(text(500)("Internal Server Error"))) with
                                | Error(e3) -> Error(e3)
                                | Ok(_n2) -> await Ashes.Net.Tcp.close(client)
        in connLoop("")))
