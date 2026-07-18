import Kv
import Resp
import Ashes.IO as io
import Ashes.Test as test
let check label expected actual =
    (let _ = test.assertEqual(expected)(actual)
    in io.writeLine("ok - " + label))

let replyOf outcome =
    match outcome with
        | (reply, _store, _quit) -> reply

let storeOf outcome =
    match outcome with
        | (_reply, store, _quit) -> store

let quitOf outcome =
    match outcome with
        | (_reply, _store, quit) -> quit

let wordsOf parsed =
    match parsed with
        | RespParsed(words, _rest) -> words
        | RespNeedMore -> []
        | RespMalformed(_msg) -> []

let seeded _ =
    Unit
    |> Kv.emptyStore
    |> Kv.setValue("name")("ashes")
    |> Kv.setValue("kind")("compiler")

let parseArrayCommand _ =
    "*3\r\n$3\r\nSET\r\n$4\r\nname\r\n$5\r\nashes\r\n"
    |> Resp.parse
    |> wordsOf
    |> Resp.array
    |> check("parses a RESP array command")(Resp.array(["SET", "name", "ashes"]))

let parseInlineCommand _ =
    "PING extra\r\n"
    |> Resp.parse
    |> wordsOf
    |> Resp.array
    |> check("parses an inline command")(Resp.array(["PING", "extra"]))

let parsePartial _ =
    check("waits for a complete command")("need more")(match Resp.parse("*2\r\n$3\r\nGET\r\n$3\r\nna") with
        | RespNeedMore -> "need more"
        | RespParsed(_words, _rest) -> "parsed"
        | RespMalformed(_msg) -> "malformed")

let pingPong _ =
    ["PING"]
    |> Kv.execute(Kv.emptyStore(Unit))
    |> replyOf
    |> check("PING answers PONG")(Resp.simple("PONG"))

let echoBack _ =
    ["echo", "hello"]
    |> Kv.execute(Kv.emptyStore(Unit))
    |> replyOf
    |> check("ECHO is case-insensitive and answers a bulk")(Resp.bulk("hello"))

let setAnswersOk _ =
    ["SET", "name", "ashes"]
    |> Kv.execute(Kv.emptyStore(Unit))
    |> replyOf
    |> check("SET answers OK")(Resp.simple("OK"))

let getFindsValue _ =
    ["GET", "name"]
    |> Kv.execute(seeded(Unit))
    |> replyOf
    |> check("GET answers the stored bulk")(Resp.bulk("ashes"))

let getMissesValue _ =
    ["GET", "missing"]
    |> Kv.execute(seeded(Unit))
    |> replyOf
    |> check("GET answers null for a missing key")(Resp.nullBulk(Unit))

let setThenGetThreadsStore _ =
    ["GET", "k"]
    |> Kv.execute(["SET", "k", "v"]
    |> Kv.execute(Kv.emptyStore(Unit))
    |> storeOf)
    |> replyOf
    |> check("the store threads from SET to GET")(Resp.bulk("v"))

let delCountsRemoved _ =
    ["DEL", "name", "missing", "kind"]
    |> Kv.execute(seeded(Unit))
    |> replyOf
    |> check("DEL answers the removed count")(Resp.number(2))

let existsCounts _ =
    ["EXISTS", "name", "missing", "kind"]
    |> Kv.execute(seeded(Unit))
    |> replyOf
    |> check("EXISTS answers the present count")(Resp.number(2))

let keysListsAll _ =
    ["KEYS", "*"]
    |> Kv.execute(seeded(Unit))
    |> replyOf
    |> check("KEYS * answers every key")(Resp.array(["name", "kind"]))

let unknownCommand _ =
    ["NOPE"]
    |> Kv.execute(Kv.emptyStore(Unit))
    |> replyOf
    |> check("an unknown command answers an error")(Resp.errorReply("unknown command 'NOPE'"))

let quitSignalsClose _ =
    check("QUIT asks the connection to close")("close")(if ["QUIT"]
    |> Kv.execute(Kv.emptyStore(Unit))
    |> quitOf
    then "close"
    else "keep")

let allPassed _ = io.print("all tests passed")

Unit
|> parseArrayCommand
|> parseInlineCommand
|> parsePartial
|> pingPong
|> echoBack
|> setAnswersOk
|> getFindsValue
|> getMissesValue
|> setThenGetThreadsStore
|> delCountsRemoved
|> existsCounts
|> keysListsAll
|> unknownCommand
|> quitSignalsClose
|> allPassed
