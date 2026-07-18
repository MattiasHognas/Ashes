import Kv as kv
import Resp as resp
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
    |> kv.emptyStore
    |> kv.setValue("name")("ashes")
    |> kv.setValue("kind")("compiler")

let parseArrayCommand _ =
    "*3\r\n$3\r\nSET\r\n$4\r\nname\r\n$5\r\nashes\r\n"
    |> resp.parse
    |> wordsOf
    |> resp.array
    |> check("parses a RESP array command")(resp.array(["SET", "name", "ashes"]))

let parseInlineCommand _ =
    "PING extra\r\n"
    |> resp.parse
    |> wordsOf
    |> resp.array
    |> check("parses an inline command")(resp.array(["PING", "extra"]))

let parsePartial _ =
    check("waits for a complete command")("need more")(match resp.parse("*2\r\n$3\r\nGET\r\n$3\r\nna") with
        | RespNeedMore -> "need more"
        | RespParsed(_words, _rest) -> "parsed"
        | RespMalformed(_msg) -> "malformed")

let pingPong _ =
    ["PING"]
    |> kv.execute(kv.emptyStore(Unit))
    |> replyOf
    |> check("PING answers PONG")(resp.simple("PONG"))

let echoBack _ =
    ["echo", "hello"]
    |> kv.execute(kv.emptyStore(Unit))
    |> replyOf
    |> check("ECHO is case-insensitive and answers a bulk")(resp.bulk("hello"))

let setAnswersOk _ =
    ["SET", "name", "ashes"]
    |> kv.execute(kv.emptyStore(Unit))
    |> replyOf
    |> check("SET answers OK")(resp.simple("OK"))

let getFindsValue _ =
    ["GET", "name"]
    |> kv.execute(seeded(Unit))
    |> replyOf
    |> check("GET answers the stored bulk")(resp.bulk("ashes"))

let getMissesValue _ =
    ["GET", "missing"]
    |> kv.execute(seeded(Unit))
    |> replyOf
    |> check("GET answers null for a missing key")(resp.nullBulk(Unit))

let setThenGetThreadsStore _ =
    ["GET", "k"]
    |> kv.execute(["SET", "k", "v"]
    |> kv.execute(kv.emptyStore(Unit))
    |> storeOf)
    |> replyOf
    |> check("the store threads from SET to GET")(resp.bulk("v"))

let delCountsRemoved _ =
    ["DEL", "name", "missing", "kind"]
    |> kv.execute(seeded(Unit))
    |> replyOf
    |> check("DEL answers the removed count")(resp.number(2))

let existsCounts _ =
    ["EXISTS", "name", "missing", "kind"]
    |> kv.execute(seeded(Unit))
    |> replyOf
    |> check("EXISTS answers the present count")(resp.number(2))

let keysListsAll _ =
    ["KEYS", "*"]
    |> kv.execute(seeded(Unit))
    |> replyOf
    |> check("KEYS * answers every key")(resp.array(["name", "kind"]))

let unknownCommand _ =
    ["NOPE"]
    |> kv.execute(kv.emptyStore(Unit))
    |> replyOf
    |> check("an unknown command answers an error")(resp.errorReply("unknown command 'NOPE'"))

let quitSignalsClose _ =
    check("QUIT asks the connection to close")("close")(if ["QUIT"]
    |> kv.execute(kv.emptyStore(Unit))
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
