import Ashes.String as str
import Ashes.Text as text

type RespParse =
    | RespNeedMore
    | RespMalformed(Str)
    | RespParsed(List(Str), Str)

let crlf = "\r\n"

let recursive reverseWords acc words =
    match words with
        | [] -> acc
        | word :: rest -> reverseWords(word :: acc)(rest)

let recursive countWords words =
    match words with
        | [] -> 0
        | _word :: rest -> 1 + countWords(rest)

let recursive dropBlank words =
    match words with
        | [] -> []
        | word :: rest ->
            if word == ""
            then dropBlank(rest)
            else word :: dropBlank(rest)

let stripCarriage line =
    (let len = str.length(line)
    in
        if len == 0
        then line
        else
            if str.substring(line)(len - 1)(1) == "\r"
            then str.take(line)(len - 1)
            else line)

let parseInline buffer =
    (let idx = str.indexOf(buffer)("\n")
    in
        if idx < 0
        then RespNeedMore
        else
            let line = stripCarriage(str.take(buffer)(idx))
            in RespParsed(dropBlank(str.split(line)(" ")))(str.drop(buffer)(idx + 1)))

let recursive parseBulks buffer n acc =
    if n == 0
    then RespParsed(reverseWords([])(acc))(buffer)
    else
        let idx = str.indexOf(buffer)(crlf)
        in
            if idx < 0
            then RespNeedMore
            else
                let header = str.take(buffer)(idx)
                in
                    if str.startsWith(header)("$") == false
                    then RespMalformed("expected bulk string")
                    else
                        match text.parseInt(str.drop(header)(1)) with
                            | Error(_bad) -> RespMalformed("invalid bulk length")
                            | Ok(len) ->
                                let rest = str.drop(buffer)(idx + 2)
                                in
                                    if str.length(rest) < len + 2
                                    then RespNeedMore
                                    else parseBulks(str.drop(rest)(len + 2))(n - 1)(str.take(rest)(len) :: acc)

let parseArray buffer =
    (let idx = str.indexOf(buffer)(crlf)
    in
        if idx < 0
        then RespNeedMore
        else
            match text.parseInt(str.drop(str.take(buffer)(idx))(1)) with
                | Error(_bad) -> RespMalformed("invalid array length")
                | Ok(count) -> parseBulks(str.drop(buffer)(idx + 2))(count)([]))

let parse buffer =
    if buffer == ""
    then RespNeedMore
    else
        if str.startsWith(buffer)("*")
        then parseArray(buffer)
        else parseInline(buffer)

let simple s = "+" + s + crlf

let errorReply msg = "-ERR " + msg + crlf

let number n = ":" + text.fromInt(n) + crlf

let bulk s = "$" + text.fromInt(text.byteLength(s)) + crlf + s + crlf

let nullBulk _ = "$-1" + crlf

let recursive bulkSeq items =
    match items with
        | [] -> ""
        | item :: rest -> bulk(item) + bulkSeq(rest)

let array items = "*" + text.fromInt(countWords(items)) + crlf + bulkSeq(items)
