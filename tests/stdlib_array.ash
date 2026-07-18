// expect: ok
import Ashes.Collection.Array
import Ashes.IO
let xs = Ashes.Collection.Array.fromList(10 :: 20 :: 30 :: [])
in
    let updated = Ashes.Collection.Array.set(1)(25)(xs)
    in
        let appended = Ashes.Collection.Array.append(40)(updated)
        in
            match Ashes.Collection.Array.get(0)(appended) with
                | None -> Ashes.IO.print("fail")
                | Some(first) ->
                    match Ashes.Collection.Array.get(1)(appended) with
                        | None -> Ashes.IO.print("fail")
                        | Some(second) ->
                            match Ashes.Collection.Array.get(3)(appended) with
                                | None -> Ashes.IO.print("fail")
                                | Some(last) ->
                                    match Ashes.Collection.Array.get(4)(appended) with
                                        | None ->
                                            if Ashes.Collection.Array.length(appended) == 4
                                            then
                                                if Ashes.Collection.Array.isEmpty(Ashes.Collection.Array.empty)
                                                then
                                                    if Ashes.Collection.Array.isEmpty(appended)
                                                    then Ashes.IO.print("fail")
                                                    else
                                                        if first == 10
                                                        then
                                                            if second == 25
                                                            then
                                                                if last == 40
                                                                then
                                                                    match Ashes.Collection.Array.toList(appended) with
                                                                        | a :: b :: c :: d :: [] ->
                                                                            if a == 10
                                                                            then
                                                                                if b == 25
                                                                                then
                                                                                    if c == 30
                                                                                    then
                                                                                        if d == 40
                                                                                        then Ashes.IO.print("ok")
                                                                                        else Ashes.IO.print("fail")
                                                                                    else Ashes.IO.print("fail")
                                                                                else Ashes.IO.print("fail")
                                                                            else Ashes.IO.print("fail")
                                                                        | _ -> Ashes.IO.print("fail")
                                                                else Ashes.IO.print("fail")
                                                            else Ashes.IO.print("fail")
                                                        else Ashes.IO.print("fail")
                                                else Ashes.IO.print("fail")
                                            else Ashes.IO.print("fail")
                                        | Some(_) -> Ashes.IO.print("fail")
