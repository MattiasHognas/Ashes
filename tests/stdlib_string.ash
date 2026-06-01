// expect: ok
import Ashes.String
import Ashes.IO
let text = "compiler"
in 
    let hasLength = Ashes.String.length(text) == 8
    in 
        let hasSubstring = Ashes.String.substring(text)(1)(3) == "omp"
        in 
            let hasIndex = Ashes.String.indexOf(text)("pile") == 3
            in 
                let missingIndex = Ashes.String.indexOf(text)("zzz") == -1
                in 
                    let hasPrefix = Ashes.String.startsWith(text)("com")
                    in 
                        let noPrefix = Ashes.String.startsWith(text)("omp") == false
                        in 
                            let hasContains = Ashes.String.contains(text)("pil")
                            in 
                                let noContains = Ashes.String.contains(text)("zip") == false
                                in 
                                    let hasSplit = Ashes.String.split("a,b,,c")(",") == ["a", "b", "", "c"]
                                    in 
                                        let hasTrim = Ashes.String.trim(" \n\tvalue\r ") == "value"
                                        in 
                                            let hasIsLetter = Ashes.String.isLetter("A")
                                            in 
                                                let hasIsDigit = Ashes.String.isDigit("7")
                                                in 
                                                    let hasIsWhiteSpace = Ashes.String.isWhiteSpace("\n")
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
