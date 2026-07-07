// expect: 5
import Ashes.IO
capability Bump =
    | up : Int -> Int

let recursive sumBumped xs = 
    match xs with
        | [] -> 0
        | h :: t -> Bump.up(h) + sumBumped(t)

let total = 
    handle sumBumped(1 :: 2 :: []) with
        | Bump.up(n) -> resume(n + 1)
        | return(r) -> r

Ashes.IO.print(total)
