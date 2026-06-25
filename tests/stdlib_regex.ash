// expect: ok
import Ashes.Regex
let exactA = Ashes.Regex.matches(RChar("a"))("a")
in 
    let noMatch = Ashes.Regex.matches(RChar("a"))("b")
    in 
        let seqPattern = RSeq(RChar("h"))(RChar("i"))
        in 
            let seqMatch = Ashes.Regex.matches(seqPattern)("hi")
            in 
                let noSeqMatch = Ashes.Regex.matches(seqPattern)("hello")
                in 
                    if exactA
                    then 
                        if noMatch
                        then Ashes.IO.print("fail")
                        else 
                            if seqMatch
                            then 
                                if noSeqMatch
                                then Ashes.IO.print("fail")
                                else Ashes.IO.print("ok")
                            else Ashes.IO.print("fail")
                    else Ashes.IO.print("fail")
