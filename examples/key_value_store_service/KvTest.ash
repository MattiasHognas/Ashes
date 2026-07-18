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
    |> emptyStore
    |> setValue("name")("ashes")
    |> setValue("kind")("compiler")

let parseArrayCommand _ =
    "*3\r\n$3\r\nSET\r\n$4\r\nname\r\n$5\r\nashes\r\n"
    |> parse
    |> wordsOf
    |> array
    |> check("parses a RESP array command")(array(["SET", "name", "ashes"]))

let parseInlineCommand _ =
    "PING extra\r\n"
    |> parse
    |> wordsOf
    |> array
    |> check("parses an inline command")(array(["PING", "extra"]))

let parsePartial _ =
    check("waits for a complete command")("need more")(match parse("*2\r\n$3\r\nGET\r\n$3\r\nna") with
        | RespNeedMore -> "need more"
        | RespParsed(_words, _rest) -> "parsed"
        | RespMalformed(_msg) -> "malformed")

let pingPong _ =
    ["PING"]
    |> execute(emptyStore(Unit))
    |> replyOf
    |> check("PING answers PONG")(simple("PONG"))

let echoBack _ =
    ["echo", "hello"]
    |> execute(emptyStore(Unit))
    |> replyOf
    |> check("ECHO is case-insensitive and answers a bulk")(bulk("hello"))

let setAnswersOk _ =
    ["SET", "name", "ashes"]
    |> execute(emptyStore(Unit))
    |> replyOf
    |> check("SET answers OK")(simple("OK"))

let getFindsValue _ =
    ["GET", "name"]
    |> execute(seeded(Unit))
    |> replyOf
    |> check("GET answers the stored bulk")(bulk("ashes"))

let getMissesValue _ =
    ["GET", "missing"]
    |> execute(seeded(Unit))
    |> replyOf
    |> check("GET answers null for a missing key")(nullBulk(Unit))

let setThenGetThreadsStore _ =
    ["GET", "k"]
    |> execute(["SET", "k", "v"]
    |> execute(emptyStore(Unit))
    |> storeOf)
    |> replyOf
    |> check("the store threads from SET to GET")(bulk("v"))

let delCountsRemoved _ =
    ["DEL", "name", "missing", "kind"]
    |> execute(seeded(Unit))
    |> replyOf
    |> check("DEL answers the removed count")(number(2))

let existsCounts _ =
    ["EXISTS", "name", "missing", "kind"]
    |> execute(seeded(Unit))
    |> replyOf
    |> check("EXISTS answers the present count")(number(2))

let keysListsAll _ =
    ["KEYS", "*"]
    |> execute(seeded(Unit))
    |> replyOf
    |> check("KEYS * answers every key")(array(["name", "kind"]))

let unknownCommand _ =
    ["NOPE"]
    |> execute(emptyStore(Unit))
    |> replyOf
    |> check("an unknown command answers an error")(errorReply("unknown command 'NOPE'"))

let quitSignalsClose _ =
    check("QUIT asks the connection to close")("close")(if ["QUIT"]
    |> execute(emptyStore(Unit))
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
