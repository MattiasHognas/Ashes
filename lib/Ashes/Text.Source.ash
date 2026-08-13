type PositionEncoding =
    | Utf8
    | Utf16
    | UnicodeScalar

type Position =
    | line: Int
    | character: Int

let isContinuation value =
    if value >= 128
    then value < 192
    else false

let isBoundary text offset =
    (let bytes = Ashes.Byte.fromText(text)
    in
        let length = Ashes.Byte.length(bytes)
        in
            if offset < 0
            then false
            else
                if offset > length
                then false
                else
                    if offset == 0
                    then true
                    else
                        if offset == length
                        then true
                        else
                            if isContinuation(Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(offset)))
                            then false
                            else true)

let recursive clampBoundary bytes offset =
    if offset <= 0
    then 0
    else
        if isContinuation(Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(offset)))
        then clampBoundary(bytes)(offset - 1)
        else offset

let scalarBytes first =
    if first < 128
    then 1
    else
        if first < 224
        then 2
        else
            if first < 240
            then 3
            else 4

let characterWidth encoding first =
    match encoding with
        | Utf8 -> scalarBytes(first)
        | Utf16 ->
            if first >= 240
            then 2
            else 1
        | UnicodeScalar -> 1

let byteOffsetToPosition text encoding offset =
    (let bytes = Ashes.Byte.fromText(text)
    in
        let length = Ashes.Byte.length(bytes)
        in
            let bounded =
                if offset <= 0
                then 0
                else
                    if offset >= length
                    then length
                    else clampBoundary(bytes)(offset)
            in
                let recursive go i line character =
                    if i >= bounded
                    then Position(line = line, character = character)
                    else
                        let first = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(i))
                        in
                            if first == 13
                            then
                                if i + 1 < length
                                then
                                    if Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(i + 1)) == 10
                                    then
                                        if bounded <= i + 1
                                        then Position(line = line, character = character)
                                        else go(i + 2)(line + 1)(0)
                                    else go(i + 1)(line + 1)(0)
                                else go(i + 1)(line + 1)(0)
                            else
                                if first == 10
                                then go(i + 1)(line + 1)(0)
                                else go(i + scalarBytes(first))(line)(character + characterWidth(encoding)(first))
                in go(0)(0)(0))

let positionToByteOffset text encoding position =
    (let bytes = Ashes.Byte.fromText(text)
    in
        let length = Ashes.Byte.length(bytes)
        in
            let targetLine =
                if position.line < 0
                then 0
                else position.line
            in
                let targetCharacter =
                    if position.character < 0
                    then 0
                    else position.character
                in
                    let recursive findLine i line =
                        if i >= length
                        then i
                        else
                            if line >= targetLine
                            then i
                            else
                                let first = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(i))
                                in
                                    if first == 13
                                    then
                                        if i + 1 < length
                                        then
                                            if Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(i + 1)) == 10
                                            then findLine(i + 2)(line + 1)
                                            else findLine(i + 1)(line + 1)
                                        else findLine(i + 1)(line + 1)
                                    else
                                        if first == 10
                                        then findLine(i + 1)(line + 1)
                                        else findLine(i + scalarBytes(first))(line)
                    in
                        let lineStart = findLine(0)(0)
                        in
                            let recursive findCharacter i character =
                                if i >= length
                                then i
                                else
                                    if character >= targetCharacter
                                    then i
                                    else
                                        let first = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(i))
                                        in
                                            if first == 10
                                            then i
                                            else
                                                if first == 13
                                                then i
                                                else
                                                    let width = characterWidth(encoding)(first)
                                                    in
                                                        if character + width > targetCharacter
                                                        then i
                                                        else findCharacter(i + scalarBytes(first))(character + width)
                            in findCharacter(lineStart)(0))
