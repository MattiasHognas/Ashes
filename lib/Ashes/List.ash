let foldLeft = 
    fun (f) -> 
        fun (initial) -> 
            fun (xs) -> 
                let rec go = 
                    fun (acc) -> 
                        fun (rest) -> 
                            match rest with
                                | [] -> acc
                                | head :: tail -> go(f(acc)(head))(tail)
                in go(initial)(xs)

let fold = foldLeft

let reverse = 
    fun (xs) -> 
        let rec go = 
            fun (acc) -> 
                fun (rest) -> 
                    match rest with
                        | [] -> acc
                        | head :: tail -> go(head :: acc)(tail)
        in go([])(xs)

let length = 
    fun (xs) -> 
        let rec go = 
            fun (acc) -> 
                fun (rest) -> 
                    match rest with
                        | [] -> acc
                        | _ :: tail -> go(acc + 1)(tail)
        in go(0)(xs)

let map = 
    fun (f) -> 
        let rec mapGo = 
            fun (xs) -> 
                match xs with
                    | [] -> []
                    | head :: tail -> f(head) :: mapGo(tail)
        in mapGo

let rec filter = 
    fun (predicate) -> 
        fun (xs) -> 
            match xs with
                | [] -> []
                | head :: tail -> 
                    if predicate(head)
                    then head :: filter(predicate)(tail)
                    else filter(predicate)(tail)

let append = 
    fun (left) -> 
        fun (right) -> 
            let rec go = 
                fun (rest) -> 
                    match rest with
                        | [] -> right
                        | head :: tail -> head :: go(tail)
            in go(left)

let isEmpty = 
    fun (xs) -> 
        match xs with
            | [] -> true
            | _ :: _ -> false

let head = 
    fun (xs) -> 
        match xs with
            | [] -> None
            | item :: _ -> Some(item)

let tail = 
    fun (xs) -> 
        match xs with
            | [] -> None
            | _ :: rest -> Some(rest)
