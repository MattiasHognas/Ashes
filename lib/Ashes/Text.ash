let recursive length text =
    match Ashes.Text.uncons(text) with
        | None -> 0
        | Some((_head, tail)) -> 1 + length(tail)

let recursive drop text count =
    if count <= 0
    then text
    else
        match Ashes.Text.uncons(text) with
            | None -> ""
            | Some((_head, tail)) -> drop(tail)(count - 1)

let recursive take text count =
    if count <= 0
    then ""
    else
        match Ashes.Text.uncons(text) with
            | None -> ""
            | Some((head, tail)) -> head + take(tail)(count - 1)

let recursive cpByteOffset bytes i cpSeen targetCp limit =
    if i >= limit
    then limit
    else
        let b = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(i))
        in
            let isStart =
                if b < 128
                then true
                else b >= 192
            in
                if isStart
                then
                    if cpSeen == targetCp
                    then i
                    else cpByteOffset(bytes)(i + 1)(cpSeen + 1)(targetCp)(limit)
                else cpByteOffset(bytes)(i + 1)(cpSeen)(targetCp)(limit)

let substring text start count =
    if start <= -1
    then ""
    else
        if count <= 0
        then ""
        else
            let bytes = Ashes.Byte.fromText(text)
            in
                let limit = Ashes.Byte.length(bytes)
                in
                    let byteStart = cpByteOffset(bytes)(0)(0)(start)(limit)
                    in
                        let byteEnd = cpByteOffset(bytes)(0)(0)(start + count)(limit)
                        in Ashes.Byte.subText(bytes)(byteStart)(byteEnd - byteStart)

let recursive countCodepoints bytes i limit acc =
    if i >= limit
    then acc
    else
        let b = Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(i))
        in
            let isStart =
                if b < 128
                then true
                else b >= 192
            in
                countCodepoints(bytes)(i + 1)(limit)(if isStart
                then acc + 1
                else acc)

let recursive byteFind tb needle from tlen nlen firstByte =
    (let idx = Ashes.Byte.indexOf(tb)(firstByte)(from)
    in
        if idx < 0
        then -1
        else
            if idx + nlen > tlen
            then -1
            else
                if Ashes.Byte.subView(tb)(idx)(nlen) == needle
                then idx
                else byteFind(tb)(needle)(idx + 1)(tlen)(nlen)(firstByte))

let firstByteOf text = Ashes.Number.UInt.toInt(Ashes.Byte.get(Ashes.Byte.fromText(text))(0))

let startsWith text prefix =
    (let nlen = Ashes.Text.byteLength(prefix)
    in
        if nlen == 0
        then true
        else
            let tb = Ashes.Byte.fromText(text)
            in
                if nlen > Ashes.Byte.length(tb)
                then false
                else Ashes.Byte.subView(tb)(0)(nlen) == prefix)

let indexOf text needle =
    if needle == ""
    then 0
    else
        let tb = Ashes.Byte.fromText(text)
        in
            let bytePos = byteFind(tb)(needle)(0)(Ashes.Byte.length(tb))(Ashes.Text.byteLength(needle))(firstByteOf(needle))
            in
                if bytePos < 0
                then -1
                else countCodepoints(tb)(0)(bytePos)(0)

let contains text needle =
    if needle == ""
    then true
    else
        let tb = Ashes.Byte.fromText(text)
        in byteFind(tb)(needle)(0)(Ashes.Byte.length(tb))(Ashes.Text.byteLength(needle))(firstByteOf(needle)) >= 0

let split text separator =
    if separator == ""
    then [text]
    else
        let tb = Ashes.Byte.fromText(text)
        in
            let tlen = Ashes.Byte.length(tb)
            in
                let slen = Ashes.Text.byteLength(separator)
                in
                    let sfirst = firstByteOf(separator)
                    in
                        let recursive go from =
                            let idx = byteFind(tb)(separator)(from)(tlen)(slen)(sfirst)
                            in
                                if idx < 0
                                then [Ashes.Byte.subText(tb)(from)(tlen - from)]
                                else Ashes.Byte.subText(tb)(from)(idx - from) :: go(idx + slen)
                        in go(0)

let isDigit text =
    match text with
        | "0" -> true
        | "1" -> true
        | "2" -> true
        | "3" -> true
        | "4" -> true
        | "5" -> true
        | "6" -> true
        | "7" -> true
        | "8" -> true
        | "9" -> true
        | _ -> false

let isLetter text =
    match text with
        | "a" -> true
        | "b" -> true
        | "c" -> true
        | "d" -> true
        | "e" -> true
        | "f" -> true
        | "g" -> true
        | "h" -> true
        | "i" -> true
        | "j" -> true
        | "k" -> true
        | "l" -> true
        | "m" -> true
        | "n" -> true
        | "o" -> true
        | "p" -> true
        | "q" -> true
        | "r" -> true
        | "s" -> true
        | "t" -> true
        | "u" -> true
        | "v" -> true
        | "w" -> true
        | "x" -> true
        | "y" -> true
        | "z" -> true
        | "A" -> true
        | "B" -> true
        | "C" -> true
        | "D" -> true
        | "E" -> true
        | "F" -> true
        | "G" -> true
        | "H" -> true
        | "I" -> true
        | "J" -> true
        | "K" -> true
        | "L" -> true
        | "M" -> true
        | "N" -> true
        | "O" -> true
        | "P" -> true
        | "Q" -> true
        | "R" -> true
        | "S" -> true
        | "T" -> true
        | "U" -> true
        | "V" -> true
        | "W" -> true
        | "X" -> true
        | "Y" -> true
        | "Z" -> true
        | _ -> false

let isWhiteSpace text =
    match text with
        | " " -> true
        | "\t" -> true
        | "\n" -> true
        | "\r" -> true
        | _ -> false

let isWhiteSpaceByte b =
    if b == 32
    then true
    else
        if b == 9
        then true
        else
            if b == 10
            then true
            else b == 13

let trimStart text =
    (let tb = Ashes.Byte.fromText(text)
    in
        let n = Ashes.Byte.length(tb)
        in
            let recursive go i =
                if i >= n
                then ""
                else
                    if isWhiteSpaceByte(Ashes.Number.UInt.toInt(Ashes.Byte.get(tb)(i)))
                    then go(i + 1)
                    else Ashes.Byte.subText(tb)(i)(n - i)
            in go(0))

let recursive lastAndInit text =
    match Ashes.Text.uncons(text) with
        | None -> None
        | Some((head, tail)) ->
            match Ashes.Text.uncons(tail) with
                | None -> Some(("", head))
                | Some((_tailHead, _tailRest)) ->
                    match lastAndInit(tail) with
                        | None -> None
                        | Some((init, last)) -> Some((head + init, last))

let trimEnd text =
    (let tb = Ashes.Byte.fromText(text)
    in
        let recursive go j =
            if j <= 0
            then ""
            else
                if isWhiteSpaceByte(Ashes.Number.UInt.toInt(Ashes.Byte.get(tb)(j - 1)))
                then go(j - 1)
                else Ashes.Byte.subText(tb)(0)(j)
        in go(Ashes.Byte.length(tb)))

let trim text = trimEnd(trimStart(text))

let compare left right = Ashes.Byte.compare(Ashes.Byte.fromText(left))(Ashes.Byte.fromText(right))

let join separator parts =
    (let recursive rev acc xs =
        match xs with
            | [] -> acc
            | head :: tail -> rev(head :: acc)(tail)
    in
        let recursive interleaveGo acc ps =
            match ps with
                | [] -> rev([])(acc)
                | first :: rest ->
                    match acc with
                        | [] -> interleaveGo(first :: [])(rest)
                        | _ -> interleaveGo(first :: separator :: acc)(rest)
        in
            let recursive pairwiseGo acc ps =
                match ps with
                    | [] -> rev([])(acc)
                    | first :: rest ->
                        match rest with
                            | [] -> rev([])(first :: acc)
                            | second :: more ->
                                let merged = first + second
                                in pairwiseGo(merged :: acc)(more)
            in
                let recursive reduce ps =
                    match ps with
                        | [] -> ""
                        | only :: rest ->
                            match rest with
                                | [] -> only
                                | _ -> reduce(pairwiseGo([])(ps))
                in reduce(interleaveGo([])(parts)))
