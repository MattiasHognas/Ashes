let rec length = 
    fun (text) -> 
        match Ashes.Text.uncons(text) with
            | None -> 0
            | Some((_head, tail)) -> 1 + length(tail)

let rec drop = 
    fun (text) -> 
        fun (count) -> 
            if count <= 0
            then text
            else 
                match Ashes.Text.uncons(text) with
                    | None -> ""
                    | Some((_head, tail)) -> drop(tail)(count - 1)

let rec take = 
    fun (text) -> 
        fun (count) -> 
            if count <= 0
            then ""
            else 
                match Ashes.Text.uncons(text) with
                    | None -> ""
                    | Some((head, tail)) -> head + take(tail)(count - 1)

let substring = 
    fun (text) -> 
        fun (start) -> 
            fun (count) -> 
                if start <= -1
                then ""
                else 
                    if count <= 0
                    then ""
                    else take(drop(text)(start))(count)

let rec startsWith = 
    fun (text) -> 
        fun (prefix) -> 
            match Ashes.Text.uncons(prefix) with
                | None -> true
                | Some((prefixHead, prefixTail)) -> 
                    match Ashes.Text.uncons(text) with
                        | None -> false
                        | Some((textHead, textTail)) -> 
                            if textHead == prefixHead
                            then startsWith(textTail)(prefixTail)
                            else false

let indexOf = 
    fun (text) -> 
        fun (needle) -> 
            if needle == ""
            then 0
            else 
                let rec go remaining offset = 
                    if startsWith(remaining)(needle)
                    then offset
                    else 
                        match Ashes.Text.uncons(remaining) with
                            | None -> -1
                            | Some((_head, tail)) -> go(tail)(offset + 1)
                in go(text)(0)

let contains = 
    fun (text) -> 
        fun (needle) -> indexOf(text)(needle) >= 0

let split = 
    fun (text) -> 
        fun (separator) -> 
            if separator == ""
            then [text]
            else 
                let separatorLength = length(separator)
                in 
                    let rec go remaining = 
                        let foundAt = indexOf(remaining)(separator)
                        in 
                            if foundAt <= -1
                            then [remaining]
                            else 
                                let piece = substring(remaining)(0)(foundAt)
                                in 
                                    let restStart = foundAt + separatorLength
                                    in 
                                        let restLen = length(remaining) - restStart
                                        in 
                                            let rest = substring(remaining)(restStart)(restLen)
                                            in piece :: go(rest)
                    in go(text)

let isDigit = 
    fun (text) -> 
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

let isLetter = 
    fun (text) -> 
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

let isWhiteSpace = 
    fun (text) -> 
        match text with
            | " " -> true
            | "\t" -> true
            | "\n" -> true
            | "\r" -> true
            | _ -> false

let rec trimStart = 
    fun (text) -> 
        match Ashes.Text.uncons(text) with
            | None -> ""
            | Some((head, tail)) -> 
                if isWhiteSpace(head)
                then trimStart(tail)
                else text

let rec lastAndInit = 
    fun (text) -> 
        match Ashes.Text.uncons(text) with
            | None -> None
            | Some((head, tail)) -> 
                match Ashes.Text.uncons(tail) with
                    | None -> Some(("", head))
                    | Some((_tailHead, _tailRest)) -> 
                        match lastAndInit(tail) with
                            | None -> None
                            | Some((init, last)) -> Some((head + init, last))

let rec trimEnd = 
    fun (text) -> 
        match lastAndInit(text) with
            | None -> ""
            | Some((init, last)) -> 
                if isWhiteSpace(last)
                then trimEnd(init)
                else text

let trim = 
    fun (text) -> trimEnd(trimStart(text))
