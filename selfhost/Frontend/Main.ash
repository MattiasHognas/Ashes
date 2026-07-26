import Tokens
import Lexer
import Ashes.IO as io
import Ashes.IO.File as file
import Ashes.Text.Json as json
let tokenToJson tok = json.JsonObject("kind")(json.JsonStr(tokenKindName(tok.kind)))(json.JsonObject("text")(json.JsonStr(tok.text))(json.JsonObject("intValue")(json.JsonInt(tok.intValue))(json.JsonObject("floatValue")(json.JsonFloat(tok.floatValue))(json.JsonObject("position")(json.JsonInt(tok.position))(json.JsonObject("length")(json.JsonInt(tok.length))(json.JsonObjectEnd))))))

let recursive tokensToJson toks =
    match toks with
        | [] -> json.JsonArrayEnd
        | t :: rest -> json.JsonArray(tokenToJson(t))(tokensToJson(rest))

let dumpFile path =
    match file.readText(path) with
        | Error(msg) -> "ERROR:" + msg
        | Ok(source) -> json.stringify(tokensToJson(tokenize(source)))

match io.args with
    | [] -> io.print("usage: lex-dump <file.ash>")
    | path :: _rest -> io.print(dumpFile(path))
