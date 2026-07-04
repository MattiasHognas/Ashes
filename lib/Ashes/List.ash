let foldLeft f initial xs = 
    (let recursive go acc rest = 
        match rest with
            | [] -> acc
            | head :: tail -> go(f(acc)(head))(tail)
    in go(initial)(xs))

let fold = foldLeft

let reverse xs = 
    (let recursive go acc rest = 
        match rest with
            | [] -> acc
            | head :: tail -> go(head :: acc)(tail)
    in go([])(xs))

let length xs = 
    (let recursive go acc rest = 
        match rest with
            | [] -> acc
            | _ :: tail -> go(acc + 1)(tail)
    in go(0)(xs))

let map f = 
    (let recursive mapGo xs = 
        match xs with
            | [] -> []
            | head :: tail -> f(head) :: mapGo(tail)
    in mapGo)

let recursive filter predicate xs = 
    match xs with
        | [] -> []
        | head :: tail -> 
            if predicate(head)
            then head :: filter(predicate)(tail)
            else filter(predicate)(tail)

let append left right = 
    (let recursive go rest = 
        match rest with
            | [] -> right
            | head :: tail -> head :: go(tail)
    in go(left))

let isEmpty xs = 
    match xs with
        | [] -> true
        | _ :: _ -> false

let head xs = 
    match xs with
        | [] -> None
        | item :: _ -> Some(item)

let tail xs = 
    match xs with
        | [] -> None
        | _ :: rest -> Some(rest)
