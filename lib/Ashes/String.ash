import Ashes.List
let rec length = 
    fun (text) -> 
        match Ashes.Text.uncons(text) with
            | None -> 0
            | Some((_head, tail)) -> 1 + length(tail)
in 
    let rec drop = 
        fun (text) -> 
            fun (count) -> 
                if count <= 0
                then text
                else 
                    match Ashes.Text.uncons(text) with
                        | None -> ""
                        | Some((_head, tail)) -> drop(tail)(count - 1)
    in 
        let rec take = 
            fun (text) -> 
                fun (count) -> 
                    if count <= 0
                    then ""
                    else 
                        match Ashes.Text.uncons(text) with
                            | None -> ""
                            | Some((head, tail)) -> head + take(tail)(count - 1)
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
                                                        fun (acc) -> 
                                                            let foundAt = indexOf(remaining)(separator)
                                                            in 
                                                                if foundAt <= -1
                                                                then Ashes.List.reverse(remaining :: acc)
                                                                else 
                                                                    let head = substring(remaining)(0)(foundAt)
                                                                    in 
                                                                        let restStart = foundAt + separatorLength
                                                                        in 
                                                                            let restLength = length(remaining) - restStart
                                                                            in 
                                                                                let tail = substring(remaining)(restStart)(restLength)
                                                                                in go(tail)(head :: acc)
                                                in go(text)([])
                            in 
                                let isDigit = 
                                    fun (text) -> 
                                        match Ashes.Text.uncons(text) with
                                            | None -> false
                                            | Some((head, tail)) -> 
                                                if tail == ""
                                                then 
                                                    if head == "0"
                                                    then true
                                                    else 
                                                        if head == "1"
                                                        then true
                                                        else 
                                                            if head == "2"
                                                            then true
                                                            else 
                                                                if head == "3"
                                                                then true
                                                                else 
                                                                    if head == "4"
                                                                    then true
                                                                    else 
                                                                        if head == "5"
                                                                        then true
                                                                        else 
                                                                            if head == "6"
                                                                            then true
                                                                            else 
                                                                                if head == "7"
                                                                                then true
                                                                                else 
                                                                                    if head == "8"
                                                                                    then true
                                                                                    else head == "9"
                                                else false
                                in 
                                    let isLetter = 
                                        fun (text) -> 
                                            match Ashes.Text.uncons(text) with
                                                | None -> false
                                                | Some((head, tail)) -> 
                                                    if tail == ""
                                                    then 
                                                        if head == "a"
                                                        then true
                                                        else 
                                                            if head == "b"
                                                            then true
                                                            else 
                                                                if head == "c"
                                                                then true
                                                                else 
                                                                    if head == "d"
                                                                    then true
                                                                    else 
                                                                        if head == "e"
                                                                        then true
                                                                        else 
                                                                            if head == "f"
                                                                            then true
                                                                            else 
                                                                                if head == "g"
                                                                                then true
                                                                                else 
                                                                                    if head == "h"
                                                                                    then true
                                                                                    else 
                                                                                        if head == "i"
                                                                                        then true
                                                                                        else 
                                                                                            if head == "j"
                                                                                            then true
                                                                                            else 
                                                                                                if head == "k"
                                                                                                then true
                                                                                                else 
                                                                                                    if head == "l"
                                                                                                    then true
                                                                                                    else 
                                                                                                        if head == "m"
                                                                                                        then true
                                                                                                        else 
                                                                                                            if head == "n"
                                                                                                            then true
                                                                                                            else 
                                                                                                                if head == "o"
                                                                                                                then true
                                                                                                                else 
                                                                                                                    if head == "p"
                                                                                                                    then true
                                                                                                                    else 
                                                                                                                        if head == "q"
                                                                                                                        then true
                                                                                                                        else 
                                                                                                                            if head == "r"
                                                                                                                            then true
                                                                                                                            else 
                                                                                                                                if head == "s"
                                                                                                                                then true
                                                                                                                                else 
                                                                                                                                    if head == "t"
                                                                                                                                    then true
                                                                                                                                    else 
                                                                                                                                        if head == "u"
                                                                                                                                        then true
                                                                                                                                        else 
                                                                                                                                            if head == "v"
                                                                                                                                            then true
                                                                                                                                            else 
                                                                                                                                                if head == "w"
                                                                                                                                                then true
                                                                                                                                                else 
                                                                                                                                                    if head == "x"
                                                                                                                                                    then true
                                                                                                                                                    else 
                                                                                                                                                        if head == "y"
                                                                                                                                                        then true
                                                                                                                                                        else 
                                                                                                                                                            if head == "z"
                                                                                                                                                            then true
                                                                                                                                                            else 
                                                                                                                                                                if head == "A"
                                                                                                                                                                then true
                                                                                                                                                                else 
                                                                                                                                                                    if head == "B"
                                                                                                                                                                    then true
                                                                                                                                                                    else 
                                                                                                                                                                        if head == "C"
                                                                                                                                                                        then true
                                                                                                                                                                        else 
                                                                                                                                                                            if head == "D"
                                                                                                                                                                            then true
                                                                                                                                                                            else 
                                                                                                                                                                                if head == "E"
                                                                                                                                                                                then true
                                                                                                                                                                                else 
                                                                                                                                                                                    if head == "F"
                                                                                                                                                                                    then true
                                                                                                                                                                                    else 
                                                                                                                                                                                        if head == "G"
                                                                                                                                                                                        then true
                                                                                                                                                                                        else 
                                                                                                                                                                                            if head == "H"
                                                                                                                                                                                            then true
                                                                                                                                                                                            else 
                                                                                                                                                                                                if head == "I"
                                                                                                                                                                                                then true
                                                                                                                                                                                                else 
                                                                                                                                                                                                    if head == "J"
                                                                                                                                                                                                    then true
                                                                                                                                                                                                    else 
                                                                                                                                                                                                        if head == "K"
                                                                                                                                                                                                        then true
                                                                                                                                                                                                        else 
                                                                                                                                                                                                            if head == "L"
                                                                                                                                                                                                            then true
                                                                                                                                                                                                            else 
                                                                                                                                                                                                                if head == "M"
                                                                                                                                                                                                                then true
                                                                                                                                                                                                                else 
                                                                                                                                                                                                                    if head == "N"
                                                                                                                                                                                                                    then true
                                                                                                                                                                                                                    else 
                                                                                                                                                                                                                        if head == "O"
                                                                                                                                                                                                                        then true
                                                                                                                                                                                                                        else 
                                                                                                                                                                                                                            if head == "P"
                                                                                                                                                                                                                            then true
                                                                                                                                                                                                                            else 
                                                                                                                                                                                                                                if head == "Q"
                                                                                                                                                                                                                                then true
                                                                                                                                                                                                                                else 
                                                                                                                                                                                                                                    if head == "R"
                                                                                                                                                                                                                                    then true
                                                                                                                                                                                                                                    else 
                                                                                                                                                                                                                                        if head == "S"
                                                                                                                                                                                                                                        then true
                                                                                                                                                                                                                                        else 
                                                                                                                                                                                                                                            if head == "T"
                                                                                                                                                                                                                                            then true
                                                                                                                                                                                                                                            else 
                                                                                                                                                                                                                                                if head == "U"
                                                                                                                                                                                                                                                then true
                                                                                                                                                                                                                                                else 
                                                                                                                                                                                                                                                    if head == "V"
                                                                                                                                                                                                                                                    then true
                                                                                                                                                                                                                                                    else 
                                                                                                                                                                                                                                                        if head == "W"
                                                                                                                                                                                                                                                        then true
                                                                                                                                                                                                                                                        else 
                                                                                                                                                                                                                                                            if head == "X"
                                                                                                                                                                                                                                                            then true
                                                                                                                                                                                                                                                            else 
                                                                                                                                                                                                                                                                if head == "Y"
                                                                                                                                                                                                                                                                then true
                                                                                                                                                                                                                                                                else head == "Z"
                                                    else false
                                    in 
                                        let isWhiteSpace = 
                                            fun (text) -> 
                                                match Ashes.Text.uncons(text) with
                                                    | None -> false
                                                    | Some((head, tail)) -> 
                                                        if tail == ""
                                                        then 
                                                            if head == " "
                                                            then true
                                                            else 
                                                                if head == "\t"
                                                                then true
                                                                else 
                                                                    if head == "\n"
                                                                    then true
                                                                    else head == "\r"
                                                        else false
                                        in 
                                            let rec trimStart = 
                                                fun (text) -> 
                                                    match Ashes.Text.uncons(text) with
                                                        | None -> ""
                                                        | Some((head, tail)) -> 
                                                            if isWhiteSpace(head)
                                                            then trimStart(tail)
                                                            else text
                                            in 
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
                                                in 
                                                    let rec trimEnd = 
                                                        fun (text) -> 
                                                            match lastAndInit(text) with
                                                                | None -> ""
                                                                | Some((init, last)) -> 
                                                                    if isWhiteSpace(last)
                                                                    then trimEnd(init)
                                                                    else text
                                                    in 
                                                        let trim = 
                                                            fun (text) -> trimEnd(trimStart(text))
                                                        in trim
