let fold = 
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
in 
    let reverse = 
        fun (xs) -> 
            let rec go = 
                fun (acc) -> 
                    fun (rest) -> 
                        match rest with
                            | [] -> acc
                            | head :: tail -> go(head :: acc)(tail)
            in go([])(xs)
    in 
        let length = 
            fun (xs) -> 
                let rec go = 
                    fun (acc) -> 
                        fun (rest) -> 
                            match rest with
                                | [] -> acc
                                | _ :: tail -> go(acc + 1)(tail)
                in go(0)(xs)
        in 
            let map = 
                fun (f) -> 
                    let rec mapGo = 
                        fun (xs) -> 
                            match xs with
                                | [] -> []
                                | head :: tail -> f(head) :: mapGo(tail)
                    in mapGo
            in 
                let filter = 
                    let rec go = 
                        fun (predicate) -> 
                            fun (xs) -> 
                                match xs with
                                    | [] -> []
                                    | head :: tail -> 
                                        if predicate(head)
                                        then head :: go(predicate)(tail)
                                        else go(predicate)(tail)
                    in go
                in 
                    let append = 
                        fun (left) -> 
                            fun (right) -> 
                                let rec go = 
                                    fun (rest) -> 
                                        match rest with
                                            | [] -> right
                                            | head :: tail -> head :: go(tail)
                                in go(left)
                    in append
