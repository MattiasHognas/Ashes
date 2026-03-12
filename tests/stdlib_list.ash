// expect: ok
import Ashes.List
import Ashes.IO
let nums = [1, 2, 3]
in 
    let mapped = 
        Ashes.List.map(fun (x) -> x + 1)(nums)
    in 
        let filtered = 
            Ashes.List.filter(fun (x) -> x >= 2)(nums)
        in 
            let folded = 
                Ashes.List.foldLeft(fun (acc) -> 
                    fun (x) -> acc + x)(0)(nums)
            in 
                let reversed = Ashes.List.reverse(nums)
                in 
                    let appended = Ashes.List.append(nums)([4, 5])
                    in 
                        match Ashes.List.head(nums) with
                            | None -> Ashes.IO.print("fail")
                            | Some(first) -> 
                                match Ashes.List.tail(nums) with
                                    | None -> Ashes.IO.print("fail")
                                    | Some(rest) -> 
                                        match Ashes.List.head(mapped) with
                                            | None -> Ashes.IO.print("fail")
                                            | Some(mappedFirst) -> 
                                                match Ashes.List.head(filtered) with
                                                    | None -> Ashes.IO.print("fail")
                                                    | Some(filteredFirst) -> 
                                                        match Ashes.List.head(reversed) with
                                                            | None -> Ashes.IO.print("fail")
                                                            | Some(reversedFirst) -> 
                                                                match Ashes.List.head(appended) with
                                                                    | None -> Ashes.IO.print("fail")
                                                                    | Some(appendedFirst) -> 
                                                                        if first == 1
                                                                        then 
                                                                            if Ashes.List.length(nums) == 3
                                                                            then 
                                                                                if mappedFirst == 2
                                                                                then 
                                                                                    if filteredFirst == 2
                                                                                    then 
                                                                                        if folded == 6
                                                                                        then 
                                                                                            if reversedFirst == 3
                                                                                            then 
                                                                                                if appendedFirst == 1
                                                                                                then 
                                                                                                    if Ashes.List.length(rest) == 2
                                                                                                    then 
                                                                                                        if Ashes.List.length(appended) == 5
                                                                                                        then 
                                                                                                            if Ashes.List.isEmpty([])
                                                                                                            then 
                                                                                                                if Ashes.List.isEmpty(nums)
                                                                                                                then Ashes.IO.print("fail")
                                                                                                                else Ashes.IO.print("ok")
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
