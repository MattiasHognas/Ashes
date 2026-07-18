// expect: ok
import Ashes.Collection.List
import Ashes.IO
let nums = [1, 2, 3]
in
    let mapped =
        Ashes.Collection.List.map(given (x) -> x + 1)(nums)
    in
        let filtered =
            Ashes.Collection.List.filter(given (x) -> x >= 2)(nums)
        in
            let folded =
                Ashes.Collection.List.foldLeft(given (acc) ->
                    given (x) -> acc + x)(0)(nums)
            in
                let reversed = Ashes.Collection.List.reverse(nums)
                in
                    let appended = Ashes.Collection.List.append(nums)([4, 5])
                    in
                        match Ashes.Collection.List.head(nums) with
                            | None -> Ashes.IO.print("fail")
                            | Some(first) ->
                                match Ashes.Collection.List.tail(nums) with
                                    | None -> Ashes.IO.print("fail")
                                    | Some(rest) ->
                                        match Ashes.Collection.List.head(mapped) with
                                            | None -> Ashes.IO.print("fail")
                                            | Some(mappedFirst) ->
                                                match Ashes.Collection.List.head(filtered) with
                                                    | None -> Ashes.IO.print("fail")
                                                    | Some(filteredFirst) ->
                                                        match Ashes.Collection.List.head(reversed) with
                                                            | None -> Ashes.IO.print("fail")
                                                            | Some(reversedFirst) ->
                                                                match Ashes.Collection.List.head(appended) with
                                                                    | None -> Ashes.IO.print("fail")
                                                                    | Some(appendedFirst) ->
                                                                        if first == 1
                                                                        then
                                                                            if Ashes.Collection.List.length(nums) == 3
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
                                                                                                    if Ashes.Collection.List.length(rest) == 2
                                                                                                    then
                                                                                                        if Ashes.Collection.List.length(appended) == 5
                                                                                                        then
                                                                                                            if Ashes.Collection.List.isEmpty([])
                                                                                                            then
                                                                                                                if Ashes.Collection.List.isEmpty(nums)
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
