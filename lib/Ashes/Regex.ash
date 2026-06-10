type Regex(S, B) =
    | REpsilon
    | RChar(S)
    | RAny
    | RClass(S, B)
    | RSeq(Regex, Regex)
    | RAlt(Regex, Regex)
    | RStar(Regex)
    | RPlus(Regex)
    | ROpt(Regex)

let lowercase = "abcdefghijklmnopqrstuvwxyz"
in
    let uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
    in
        let digits = "0123456789"
        in
            let hexDigits = "0123456789abcdefABCDEF"
            in
                let whitespace = " \t\n\r"
                in
                    let identStart = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_"
                    in
                        let identCont = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789"
                        in
                            let rec charIn =
                                fun (c) ->
                                    fun (chars) ->
                                        match Ashes.Text.uncons(chars) with
                                            | None -> false
                                            | Some((h, t)) ->
                                                if h == c then true else charIn(c)(t)
                            in
                                let rec strLen =
                                    fun (text) ->
                                        match Ashes.Text.uncons(text) with
                                            | None -> 0
                                            | Some((_h, t)) -> 1 + strLen(t)
                                in
                                    let rec strTake =
                                        fun (n) ->
                                            fun (text) ->
                                                if n <= 0 then ""
                                                else
                                                    match Ashes.Text.uncons(text) with
                                                        | None -> ""
                                                        | Some((h, t)) -> h + strTake(n - 1)(t)
                                    in
                                        let rec tryMatchStar =
                                            fun (matchFn) ->
                                                fun (regex) ->
                                                    fun (text) ->
                                                        match matchFn(regex)(text) with
                                                            | None -> Some(text)
                                                            | Some(rest) ->
                                                                if rest == text
                                                                then Some(text)
                                                                else tryMatchStar(matchFn)(regex)(rest)
                                        in
                                            let rec tryMatchHere =
                                                fun (regex) ->
                                                    fun (text) ->
                                                        match regex with
                                                            | REpsilon -> Some(text)
                                                            | RChar(c) ->
                                                                match Ashes.Text.uncons(text) with
                                                                    | None -> None
                                                                    | Some((h, t)) ->
                                                                        if h == c then Some(t) else None
                                                            | RAny ->
                                                                match Ashes.Text.uncons(text) with
                                                                    | None -> None
                                                                    | Some((_h, t)) -> Some(t)
                                                            | RClass(chars, negated) ->
                                                                match Ashes.Text.uncons(text) with
                                                                    | None -> None
                                                                    | Some((h, t)) ->
                                                                        let inSet = charIn(h)(chars)
                                                                        in
                                                                            if negated
                                                                            then if inSet then None else Some(t)
                                                                            else if inSet then Some(t) else None
                                                            | RSeq(r1, r2) ->
                                                                match tryMatchHere(r1)(text) with
                                                                    | None -> None
                                                                    | Some(rest) -> tryMatchHere(r2)(rest)
                                                            | RAlt(r1, r2) ->
                                                                match tryMatchHere(r1)(text) with
                                                                    | Some(rest) -> Some(rest)
                                                                    | None -> tryMatchHere(r2)(text)
                                                            | RStar(r) ->
                                                                tryMatchStar(tryMatchHere)(r)(text)
                                                            | RPlus(r) ->
                                                                match tryMatchHere(r)(text) with
                                                                    | None -> None
                                                                    | Some(rest) ->
                                                                        tryMatchStar(tryMatchHere)(r)(rest)
                                                            | ROpt(r) ->
                                                                match tryMatchHere(r)(text) with
                                                                    | Some(rest) -> Some(rest)
                                                                    | None -> Some(text)
                                            in
                                                let matches =
                                                    fun (regex) ->
                                                        fun (text) ->
                                                            match tryMatchHere(regex)(text) with
                                                                | None -> false
                                                                | Some(rest) -> rest == ""
                                                in
                                                    let find =
                                                        fun (regex) ->
                                                            fun (text) ->
                                                                let rec search =
                                                                    fun (remaining) ->
                                                                        match tryMatchHere(regex)(remaining) with
                                                                            | Some(rest) ->
                                                                                let matchLen = strLen(remaining) - strLen(rest)
                                                                                in Some(strTake(matchLen)(remaining))
                                                                            | None ->
                                                                                match Ashes.Text.uncons(remaining) with
                                                                                    | None -> None
                                                                                    | Some((_h, t)) -> search(t)
                                                                in search(text)
                                                    in
                                                        let rec findAll =
                                                            fun (regex) ->
                                                                fun (text) ->
                                                                    let rec go =
                                                                        fun (remaining) ->
                                                                            if remaining == ""
                                                                            then []
                                                                            else
                                                                                match tryMatchHere(regex)(remaining) with
                                                                                    | Some(rest) ->
                                                                                        if rest == remaining
                                                                                        then
                                                                                            match Ashes.Text.uncons(remaining) with
                                                                                                | None -> []
                                                                                                | Some((_h, t)) -> go(t)
                                                                                        else
                                                                                            let matchLen = strLen(remaining) - strLen(rest)
                                                                                            in
                                                                                                let matched = strTake(matchLen)(remaining)
                                                                                                in matched :: go(rest)
                                                                                    | None ->
                                                                                        match Ashes.Text.uncons(remaining) with
                                                                                            | None -> []
                                                                                            | Some((_h, t)) -> go(t)
                                                                    in go(text)
                                                        in matches
