// Ashes.Net.Http.Server — an HTTP/1.1 server layered over Ashes.Net.Tcp.Server. A handler maps a request
// to a response (handler : HttpRequest -> Task(E, HttpResponse), so handlers can `await` async work
// such as a downstream call); `serve` reads a request, runs the handler, writes the response, and —
// per HTTP/1.1 — keeps the connection alive (closing on `Connection: close`, on handler failure, or
// when the peer disconnects). Pure Ashes over the TCP layer, so it works on every target the TCP
// server does. Reads are buffered until a full request has arrived (headers complete and
// Content-Length bytes of body), so requests larger than one read and slow/split requests work.
// A Content-Length body buffers incrementally: once the headers are parsed the loop only counts
// received bytes (chunks accumulate as a list, joined once when complete), so a body arriving in
// k reads costs O(total) work, not k re-parses of a growing accumulator. Chunked bodies
// (Transfer-Encoding: chunked; chunk extensions ignored, trailers not supported) decode
// incrementally too: each read decodes only the undecoded tail (the partial frame carried
// between reads), and decoded pieces accumulate as a list joined once at the terminating frame.
//
// A response is either a buffered HttpResponse (status, extra headers, body) or a streamed
// HttpStreamed whose body is produced incrementally by a pull `step` function and framed with
// Transfer-Encoding: chunked. A request keeps its headers as the raw header block (a Str) and a
// response keeps its extra headers as a pre-rendered "Name: value\r\n..." block; the header
// accessors and builders below hide that representation.
import Ashes.Net.Tcp
import Ashes.Net.Tcp.Server
import Ashes.Text
import Ashes.Text
import Ashes.Byte
import Ashes.Number.UInt
import Ashes.Collection.List
import Ashes.Task
type HttpRequest =
    | HttpRequest(Str, Str, Str, Str)

type StreamStep =
    | StreamChunk(Str, Str)
    | StreamDone

type HttpResponse =
    | HttpResponse(Int, Str, Str)
    | HttpStreamed(Int, Str, Str -> Task(Str, StreamStep), Str)

type HttpParse =
    | HttpNeedMore
    | HttpTooLarge
    | HttpParsed(HttpRequest, Bool, Str)
    | HttpNeedBody(Str, Str, Bool, Str, Int)
    | HttpNeedChunked(Str, Str, Bool, Str, Str)

type BodyRead =
    | BodyDone(HttpRequest, Bool, Str)
    | BodyPeerClosed
    | BodyFailed(Str)
    | BodyTooLarge

type ChunkResult =
    | ChunkPartial(Str, Int)
    | ChunkDone(Str, Int)

let method req =
    match req with
        | HttpRequest(m, _p, _h, _b) -> m

let target req =
    match req with
        | HttpRequest(_m, p, _h, _b) -> p

let path req =
    (let t = target(req)
    in
        let q = Ashes.Text.indexOf(t)("?")
        in
            if q < 0
            then t
            else Ashes.Text.take(t)(q))

let rawHeaders req =
    match req with
        | HttpRequest(_m, _p, h, _b) -> h

let body req =
    match req with
        | HttpRequest(_m, _p, _h, b) -> b

let charLowerCode c =
    (let code = Ashes.Number.UInt.toInt(Ashes.Byte.get(Ashes.Byte.fromText(c))(0))
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
                let idx = Ashes.Text.indexOf(line)(":")
                in
                    if idx < 0
                    then scan(rest)
                    else
                        if sameHeaderName(Ashes.Text.trim(Ashes.Text.take(line)(idx)))(name)
                        then Some(Ashes.Text.trim(Ashes.Text.drop(line)(idx + 1)))
                        else scan(rest)
    in scan(Ashes.Text.split(block)("\r\n")))

let header req name = headerInBlock(rawHeaders(req))(name)

let respond status headerBlock bodyText = HttpResponse(status)(headerBlock)(bodyText)

let text status bodyText = HttpResponse(status)("Content-Type: text/plain; charset=utf-8\r\n")(bodyText)

let json status bodyText = HttpResponse(status)("Content-Type: application/json\r\n")(bodyText)

let withHeader name value resp =
    match resp with
        | HttpResponse(status, headerBlock, bodyText) -> HttpResponse(status)(name + ": " + value + "\r\n" + headerBlock)(bodyText)
        | HttpStreamed(status, headerBlock, step, seed) -> HttpStreamed(status)(name + ": " + value + "\r\n" + headerBlock)(step)(seed)

let requestFromLine requestLine headerBlock bodyText =
    match Ashes.Text.split(requestLine)(" ") with
        | m :: pth :: _v -> HttpRequest(m)(pth)(headerBlock)(bodyText)
        | _other -> HttpRequest("GET")("/")(headerBlock)(bodyText)

let parseRequest raw =
    (let sections = Ashes.Text.split(raw)("\r\n\r\n")
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
                    match Ashes.Text.split(headSection)("\r\n") with
                        | first :: _rest -> first
                        | [] -> ""
                in
                    let idx = Ashes.Text.indexOf(headSection)("\r\n")
                    in
                        let headerBlock =
                            if idx < 0
                            then ""
                            else Ashes.Text.drop(headSection)(idx + 2)
                        in
                            match Ashes.Text.split(requestLine)(" ") with
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
                        if status == 413
                        then "Payload Too Large"
                        else
                            if status == 500
                            then "Internal Server Error"
                            else "OK"

let renderConnection connectionValue resp =
    match resp with
        | HttpResponse(status, headerBlock, bodyText) -> "HTTP/1.1 " + Ashes.Text.fromInt(status) + " " + reasonPhrase(status) + "\r\n" + headerBlock + "Content-Length: " + Ashes.Text.fromInt(Ashes.Text.byteLength(bodyText)) + "\r\nConnection: " + connectionValue + "\r\n\r\n" + bodyText
        | HttpStreamed(_status, _headerBlock, _step, _seed) -> ""

let render resp = renderConnection("close")(resp)

let streamed status headerBlock seed step = HttpStreamed(status)(headerBlock)(step)(seed)

let hexDigitChar d =
    (let code =
        if d < 10
        then 48 + d
        else 87 + d
    in Ashes.Byte.subText(Ashes.Byte.appendByte(Ashes.Byte.fromText(""))(Ashes.Number.UInt.fromInt(code)))(0)(1))

let recursive hexOf n =
    if n <= 0
    then ""
    else hexOf(n / 16) + hexDigitChar(n - n / 16 * 16)

let toHex n =
    if n == 0
    then "0"
    else hexOf(n)

let streamHeaders connectionValue status headerBlock = "HTTP/1.1 " + Ashes.Text.fromInt(status) + " " + reasonPhrase(status) + "\r\n" + headerBlock + "Transfer-Encoding: chunked\r\nConnection: " + connectionValue + "\r\n\r\n"

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

let maxRequestBytes = 8388608

let hexVal b =
    if b >= 48
    then
        if b <= 57
        then b - 48
        else
            if b >= 97
            then
                if b <= 102
                then b - 87
                else -1
            else
                if b >= 65
                then
                    if b <= 70
                    then b - 55
                    else -1
                else -1
    else -1

let percentDecode s =
    (let bytes = Ashes.Byte.fromText(s)
    in
        let recursive go i acc =
            if i >= Ashes.Byte.length(bytes)
            then acc
            else
                let b = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(i))
                in
                    if b == 37
                    then
                        if i + 2 < Ashes.Byte.length(bytes)
                        then
                            let h1 = hexVal(Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(i + 1)))
                            in
                                let h2 = hexVal(Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(i + 2)))
                                in
                                    if h1 < 0
                                    then go(i + 1)(Ashes.Byte.appendByte(acc)(Ashes.Number.UInt.fromInt(37)))
                                    else
                                        if h2 < 0
                                        then go(i + 1)(Ashes.Byte.appendByte(acc)(Ashes.Number.UInt.fromInt(37)))
                                        else go(i + 3)(Ashes.Byte.appendByte(acc)(Ashes.Number.UInt.fromInt(h1 * 16 + h2)))
                        else go(i + 1)(Ashes.Byte.appendByte(acc)(Ashes.Number.UInt.fromInt(37)))
                    else
                        if b == 43
                        then go(i + 1)(Ashes.Byte.appendByte(acc)(Ashes.Number.UInt.fromInt(32)))
                        else go(i + 1)(Ashes.Byte.appendByte(acc)(Ashes.Byte.get(bytes)(i)))
        in
            let decoded = go(0)(Ashes.Byte.fromText(""))
            in Ashes.Byte.subText(decoded)(0)(Ashes.Byte.length(decoded)))

let query req =
    (let t = target(req)
    in
        let q = Ashes.Text.indexOf(t)("?")
        in
            if q < 0
            then ""
            else Ashes.Text.drop(t)(q + 1))

let queryParam req name =
    (let recursive scan pairs =
        match pairs with
            | [] -> None
            | pair :: rest ->
                let eq = Ashes.Text.indexOf(pair)("=")
                in
                    if eq < 0
                    then
                        if percentDecode(pair) == name
                        then Some("")
                        else scan(rest)
                    else
                        if percentDecode(Ashes.Text.take(pair)(eq)) == name
                        then Some(percentDecode(Ashes.Text.drop(pair)(eq + 1)))
                        else scan(rest)
    in scan(Ashes.Text.split(query(req))("&")))

let recursive parseHexRange bytes pos endPos acc =
    if pos >= endPos
    then acc
    else
        let b = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(pos))
        in
            let d =
                if b >= 48
                then
                    if b <= 57
                    then b - 48
                    else
                        if b >= 97
                        then
                            if b <= 102
                            then b - 87
                            else -1
                        else
                            if b >= 65
                            then
                                if b <= 70
                                then b - 55
                                else -1
                            else -1
                else -1
            in
                if d < 0
                then acc
                else parseHexRange(bytes)(pos + 1)(endPos)(acc * 16 + d)

let recursive decodeChunkedFrom allBytes pos acc =
    (let nl = Ashes.Byte.indexOf(allBytes)(10)(pos)
    in
        if nl < 0
        then ChunkPartial(acc)(pos)
        else
            let size = parseHexRange(allBytes)(pos)(nl)(0)
            in
                if size == 0
                then
                    if Ashes.Byte.length(allBytes) < nl + 3
                    then ChunkPartial(acc)(pos)
                    else ChunkDone(acc)(nl + 3)
                else
                    let dataStart = nl + 1
                    in
                        let need = dataStart + size + 2
                        in
                            if Ashes.Byte.length(allBytes) < need
                            then ChunkPartial(acc)(pos)
                            else decodeChunkedFrom(allBytes)(need)(acc + Ashes.Byte.subText(allBytes)(dataStart)(size)))

let tryParseBuffered buffered =
    (let headEnd = Ashes.Text.indexOf(buffered)("\r\n\r\n")
    in
        if headEnd < 0
        then HttpNeedMore
        else
            let headSection = Ashes.Text.take(buffered)(headEnd)
            in
                let firstLineEnd = Ashes.Text.indexOf(headSection)("\r\n")
                in
                    let requestLine =
                        if firstLineEnd < 0
                        then headSection
                        else Ashes.Text.take(headSection)(firstLineEnd)
                    in
                        let headerBlock =
                            if firstLineEnd < 0
                            then ""
                            else Ashes.Text.drop(headSection)(firstLineEnd + 2)
                        in
                            let allBytes = Ashes.Byte.fromText(buffered)
                            in
                                let headBytes = headEnd + 4
                                in
                                    let version =
                                        match Ashes.Text.split(requestLine)(" ") with
                                            | _m :: _p :: v :: _more -> v
                                            | _other -> "HTTP/1.1"
                                    in
                                        let keepAlive = keepAliveOf(headerBlock)(version)
                                        in
                                            let build =
                                                given (body) ->
                                                    given (rest) -> HttpParsed(requestFromLine(requestLine)(headerBlock)(body))(keepAlive)(rest)
                                            in
                                                let isChunked =
                                                    match headerInBlock(headerBlock)("transfer-encoding") with
                                                        | Some(te) -> sameHeaderName(Ashes.Text.trim(te))("chunked")
                                                        | None -> false
                                                in
                                                    if isChunked
                                                    then
                                                        match decodeChunkedFrom(allBytes)(headBytes)("") with
                                                            | ChunkPartial(piece, frameStart) -> HttpNeedChunked(requestLine)(headerBlock)(keepAlive)(piece)(Ashes.Byte.subText(allBytes)(frameStart)(Ashes.Byte.length(allBytes) - frameStart))
                                                            | ChunkDone(body, endOffset) -> build(body)(Ashes.Byte.subText(allBytes)(endOffset)(Ashes.Byte.length(allBytes) - endOffset))
                                                    else
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
                                                            if contentLength > maxRequestBytes
                                                            then HttpTooLarge
                                                            else
                                                                let availableBody = Ashes.Byte.length(allBytes) - headBytes
                                                                in
                                                                    if availableBody < contentLength
                                                                    then HttpNeedBody(requestLine)(headerBlock)(keepAlive)(Ashes.Byte.subText(allBytes)(headBytes)(availableBody))(contentLength)
                                                                    else build(Ashes.Byte.subText(allBytes)(headBytes)(contentLength))(Ashes.Byte.subText(allBytes)(headBytes + contentLength)(availableBody - contentLength)))

let connectionHandler handler =
    given (client) ->
        async(let recursive streamPump acc step =
            match await step(acc) with
                | Error(spe) -> Error(spe)
                | Ok(StreamDone) -> await Ashes.Net.Tcp.send(client)("0\r\n\r\n")
                | Ok(StreamChunk(bytes, next)) ->
                    match await Ashes.Net.Tcp.send(client)(toHex(Ashes.Text.byteLength(bytes)) + "\r\n" + bytes + "\r\n") with
                        | Error(sce) -> Error(sce)
                        | Ok(_scn) -> streamPump(next)(step)
        in
            let recursive deliver conn resp =
                match resp with
                    | HttpStreamed(status, headerBlock, step, seed) ->
                        match await Ashes.Net.Tcp.send(client)(streamHeaders(conn)(status)(headerBlock)) with
                            | Error(dhe) -> Error(dhe)
                            | Ok(_dhn) -> streamPump(seed)(step)
                    | HttpResponse(_s, _hh, _bb) -> await Ashes.Net.Tcp.send(client)(renderConnection(conn)(resp))
            in
                let recursive connLoop buffered =
                    if Ashes.Text.byteLength(buffered) >= maxRequestBytes
                    then
                        match await Ashes.Net.Tcp.send(client)(render(text(413)("Payload Too Large"))) with
                            | Error(e0) -> Error(e0)
                            | Ok(_n0) -> await Ashes.Net.Tcp.close(client)
                    else
                        match tryParseBuffered(buffered) with
                            | HttpTooLarge ->
                                match await Ashes.Net.Tcp.send(client)(render(text(413)("Payload Too Large"))) with
                                    | Error(e1) -> Error(e1)
                                    | Ok(_n1) -> await Ashes.Net.Tcp.close(client)
                            | HttpNeedMore ->
                                match await Ashes.Net.Tcp.receive(client)(65536) with
                                    | Error(e) -> Error(e)
                                    | Ok(chunk) ->
                                        if Ashes.Text.byteLength(chunk) == 0
                                        then await Ashes.Net.Tcp.close(client)
                                        else connLoop(buffered + chunk)
                            | HttpNeedBody(requestLine, headerBlock, keepAlive, fragment, need) ->
                                let recursive bodyLoop chunks got =
                                    if got >= need
                                    then
                                        let joined = Ashes.Text.join("")(Ashes.Collection.List.reverse(chunks))
                                        in
                                            let allBytes = Ashes.Byte.fromText(joined)
                                            in BodyDone(requestFromLine(requestLine)(headerBlock)(Ashes.Byte.subText(allBytes)(0)(need)))(keepAlive)(Ashes.Byte.subText(allBytes)(need)(got - need))
                                    else
                                        match await Ashes.Net.Tcp.receive(client)(65536) with
                                            | Error(e4) -> BodyFailed(e4)
                                            | Ok(chunk) ->
                                                if Ashes.Text.byteLength(chunk) == 0
                                                then BodyPeerClosed
                                                else bodyLoop(chunk :: chunks)(got + Ashes.Text.byteLength(chunk))
                                in
                                    match bodyLoop(fragment :: [])(Ashes.Text.byteLength(fragment)) with
                                        | BodyFailed(e5) -> Error(e5)
                                        | BodyPeerClosed -> await Ashes.Net.Tcp.close(client)
                                        | BodyTooLarge ->
                                            match await Ashes.Net.Tcp.send(client)(render(text(413)("Payload Too Large"))) with
                                                | Error(e8) -> Error(e8)
                                                | Ok(_n5) -> await Ashes.Net.Tcp.close(client)
                                        | BodyDone(req, bodyKeepAlive, rest) ->
                                            match await handler(req) with
                                                | Ok(resp) ->
                                                    match deliver(if bodyKeepAlive
                                                    then "keep-alive"
                                                    else "close")(resp) with
                                                        | Error(e6) -> Error(e6)
                                                        | Ok(_n3) ->
                                                            if bodyKeepAlive
                                                            then connLoop(rest)
                                                            else await Ashes.Net.Tcp.close(client)
                                                | Error(_he2) ->
                                                    match deliver("close")(text(500)("Internal Server Error")) with
                                                        | Error(e7) -> Error(e7)
                                                        | Ok(_n4) -> await Ashes.Net.Tcp.close(client)
                            | HttpNeedChunked(requestLine, headerBlock, keepAlive, firstPiece, firstTail) ->
                                let recursive chunkedLoop pieces tail got =
                                    if got + Ashes.Text.byteLength(tail) >= maxRequestBytes
                                    then BodyTooLarge
                                    else
                                        match await Ashes.Net.Tcp.receive(client)(65536) with
                                            | Error(e9) -> BodyFailed(e9)
                                            | Ok(chunk) ->
                                                if Ashes.Text.byteLength(chunk) == 0
                                                then BodyPeerClosed
                                                else
                                                    let tailBytes = Ashes.Byte.fromText(tail + chunk)
                                                    in
                                                        match decodeChunkedFrom(tailBytes)(0)("") with
                                                            | ChunkDone(piece, endOffset) ->
                                                                let body = Ashes.Text.join("")(Ashes.Collection.List.reverse(piece :: pieces))
                                                                in BodyDone(requestFromLine(requestLine)(headerBlock)(body))(keepAlive)(Ashes.Byte.subText(tailBytes)(endOffset)(Ashes.Byte.length(tailBytes) - endOffset))
                                                            | ChunkPartial(piece, frameStart) -> chunkedLoop(piece :: pieces)(Ashes.Byte.subText(tailBytes)(frameStart)(Ashes.Byte.length(tailBytes) - frameStart))(got + Ashes.Text.byteLength(piece))
                                in
                                    match chunkedLoop(firstPiece :: [])(firstTail)(Ashes.Text.byteLength(firstPiece)) with
                                        | BodyFailed(e10) -> Error(e10)
                                        | BodyPeerClosed -> await Ashes.Net.Tcp.close(client)
                                        | BodyTooLarge ->
                                            match await Ashes.Net.Tcp.send(client)(render(text(413)("Payload Too Large"))) with
                                                | Error(e11) -> Error(e11)
                                                | Ok(_n6) -> await Ashes.Net.Tcp.close(client)
                                        | BodyDone(req, chunkedKeepAlive, rest) ->
                                            match await handler(req) with
                                                | Ok(resp) ->
                                                    match deliver(if chunkedKeepAlive
                                                    then "keep-alive"
                                                    else "close")(resp) with
                                                        | Error(e12) -> Error(e12)
                                                        | Ok(_n7) ->
                                                            if chunkedKeepAlive
                                                            then connLoop(rest)
                                                            else await Ashes.Net.Tcp.close(client)
                                                | Error(_he3) ->
                                                    match deliver("close")(text(500)("Internal Server Error")) with
                                                        | Error(e13) -> Error(e13)
                                                        | Ok(_n8) -> await Ashes.Net.Tcp.close(client)
                            | HttpParsed(req, keepAlive, rest) ->
                                match await handler(req) with
                                    | Ok(resp) ->
                                        match deliver(if keepAlive
                                        then "keep-alive"
                                        else "close")(resp) with
                                            | Error(e2) -> Error(e2)
                                            | Ok(_n) ->
                                                if keepAlive
                                                then connLoop(rest)
                                                else await Ashes.Net.Tcp.close(client)
                                    | Error(_he) ->
                                        match deliver("close")(text(500)("Internal Server Error")) with
                                            | Error(e3) -> Error(e3)
                                            | Ok(_n2) -> await Ashes.Net.Tcp.close(client)
                in connLoop(""))

let serve port handler = Ashes.Net.Tcp.Server.serve(port)(connectionHandler(handler))

let serveParallel port workers handler = Ashes.Net.Tcp.Server.serveParallel(port)(workers)(connectionHandler(handler))
