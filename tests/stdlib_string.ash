// expect: ok
import Ashes.Text
import Ashes.IO
let text = "compiler"
in
    let hasLength = Ashes.Text.length(text) == 8
    in
        let hasSubstring = Ashes.Text.substring(text)(1)(3) == "omp"
        in
            let hasIndex = Ashes.Text.indexOf(text)("pile") == 3
            in
                let missingIndex = Ashes.Text.indexOf(text)("zzz") == -1
                in
                    let hasPrefix = Ashes.Text.startsWith(text)("com")
                    in
                        let noPrefix =
                            if Ashes.Text.startsWith(text)("omp")
                            then false
                            else true
                        in
                            let hasContains = Ashes.Text.contains(text)("pil")
                            in
                                let noContains =
                                    if Ashes.Text.contains(text)("zip")
                                    then false
                                    else true
                                in
                                    let hasSplit =
                                        match Ashes.Text.split("a,b,,c")(",") with
                                            | p0 :: p1 :: p2 :: p3 :: [] ->
                                                if p0 == "a"
                                                then
                                                    if p1 == "b"
                                                    then
                                                        if p2 == ""
                                                        then p3 == "c"
                                                        else false
                                                    else false
                                                else false
                                            | _ -> false
                                    in
                                        let hasTrim = Ashes.Text.trim(" \n\tvalue\r ") == "value"
                                        in
                                            let hasIsLetter = Ashes.Text.isLetter("A")
                                            in
                                                let hasIsDigit = Ashes.Text.isDigit("7")
                                                in
                                                    let hasIsWhiteSpace = Ashes.Text.isWhiteSpace("\n")
                                                    in
                                                        if hasLength
                                                        then
                                                            if hasSubstring
                                                            then
                                                                if hasIndex
                                                                then
                                                                    if missingIndex
                                                                    then
                                                                        if hasPrefix
                                                                        then
                                                                            if noPrefix
                                                                            then
                                                                                if hasContains
                                                                                then
                                                                                    if noContains
                                                                                    then
                                                                                        if hasSplit
                                                                                        then
                                                                                            if hasTrim
                                                                                            then
                                                                                                if hasIsLetter
                                                                                                then
                                                                                                    if hasIsDigit
                                                                                                    then
                                                                                                        if hasIsWhiteSpace
                                                                                                        then Ashes.IO.print("ok")
                                                                                                        else Ashes.IO.print("fail")
                                                                                                    else Ashes.IO.print("fail")
                                                                                                else Ashes.IO.print("fail")
                                                                                            else Ashes.IO.print("fail")
                                                                                        else Ashes.IO.print("fail")
                                                                                    else Ashes.IO.print("fail")
                                                                                else Ashes.IO.print("fail")
                                                                            else Ashes.IO.print("fail")
                                                                        else Ashes.IO.print("fail")
                                                                    else Ashes.IO.print("fail")
                                                                else Ashes.IO.print("fail")
                                                            else Ashes.IO.print("fail")
                                                        else Ashes.IO.print("fail")
