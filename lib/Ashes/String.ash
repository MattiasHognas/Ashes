let length = 
    let rec go = 
        fun (text) -> 
            match Ashes.Text.uncons(text) with
                | None -> 0
                | Some((_head, tail)) -> 1 + go(tail)
    in go
in 
    let drop = 
        let rec go = 
            fun (text) -> 
                fun (count) -> 
                    if count <= 0
                    then text
                    else 
                        match Ashes.Text.uncons(text) with
                            | None -> ""
                            | Some((_head, tail)) -> go(tail)(count - 1)
        in go
    in 
        let take = 
            let rec go = 
                fun (text) -> 
                    fun (count) -> 
                        if count <= 0
                        then ""
                        else 
                            match Ashes.Text.uncons(text) with
                                | None -> ""
                                | Some((head, tail)) -> head + go(tail)(count - 1)
            in go
        in 
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
            in 
                let startsWith = 
                    let rec go = 
                        fun (text) -> 
                            fun (prefix) -> 
                                match Ashes.Text.uncons(prefix) with
                                    | None -> true
                                    | Some((prefixHead, prefixTail)) -> 
                                        match Ashes.Text.uncons(text) with
                                            | None -> false
                                            | Some((textHead, textTail)) -> 
                                                if textHead == prefixHead
                                                then go(textTail)(prefixTail)
                                                else false
                    in go
                in 
                    let indexOf = 
                        fun (text) -> 
                            fun (needle) -> 
                                if needle == ""
                                then 0
                                else 
                                    let rec go = 
                                        fun (remaining) -> 
                                            fun (offset) -> 
                                                if startsWith(remaining)(needle)
                                                then offset
                                                else 
                                                    match Ashes.Text.uncons(remaining) with
                                                        | None -> -1
                                                        | Some((_head, tail)) -> go(tail)(offset + 1)
                                    in go(text)(0)
                    in 
                        let contains = 
                            fun (text) -> 
                                fun (needle) -> indexOf(text)(needle) >= 0
                        in 
                            let split = 
                                fun (text) -> 
                                    fun (separator) -> 
                                        if separator == ""
                                        then [text]
                                        else 
                                            let separatorLength = length(separator)
                                            in 
                                                let rec go = 
                                                    fun (remaining) -> 
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
                            in 
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
                                in 
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
                                    in 
                                        let isWhiteSpace = 
                                            fun (text) -> 
                                                match text with
                                                    | " " -> true
                                                    | "\t" -> true
                                                    | "\n" -> true
                                                    | "\r" -> true
                                                    | _ -> false
                                        in 
                                            let trimStart = 
                                                let rec go = 
                                                    fun (text) -> 
                                                        match Ashes.Text.uncons(text) with
                                                            | None -> ""
                                                            | Some((head, tail)) -> 
                                                                if isWhiteSpace(head)
                                                                then go(tail)
                                                                else text
                                                in go
                                            in 
                                                let lastAndInit = 
                                                    let rec go = 
                                                        fun (text) -> 
                                                            match Ashes.Text.uncons(text) with
                                                                | None -> None
                                                                | Some((head, tail)) -> 
                                                                    match Ashes.Text.uncons(tail) with
                                                                        | None -> Some(("", head))
                                                                        | Some((_tailHead, _tailRest)) -> 
                                                                            match go(tail) with
                                                                                | None -> None
                                                                                | Some((init, last)) -> Some((head + init, last))
                                                    in go
                                                in 
                                                    let trimEnd = 
                                                        let rec go = 
                                                            fun (text) -> 
                                                                match lastAndInit(text) with
                                                                    | None -> ""
                                                                    | Some((init, last)) -> 
                                                                        if isWhiteSpace(last)
                                                                        then go(init)
                                                                        else text
                                                        in go
                                                    in 
                                                        let trim = 
                                                            fun (text) -> trimEnd(trimStart(text))
                                                        in trim
