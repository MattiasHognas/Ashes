// A recursive producer whose tail is `head :: self(...)` is built iteratively (tail modulo
// constructor) instead of one native frame per element. This test pins the SEMANTICS the transform
// must not disturb, at sizes small enough to run under any stack: base cases, pointer-bearing
// elements, a captured callback, sharing the produced list, strict left-to-right effect order, and
// the filter/append shapes whose recursive tails are the same proof. tmc_constructor_recursion_stack
// covers the stack-depth claim itself.
// expect: 1,2,3,[]|[7]|[2,3,4]|abd-bcd-cdd|[11,21,31]|63|63|6|[2,4]|[1,2,9,8]
import Ashes.IO as io
import Ashes.Text as text
let recursive incAll xs =
    match xs with
        | [] -> []
        | head :: tail -> head + 1 :: incAll(tail)

let recursive suffixAll xs =
    match xs with
        | [] -> []
        | head :: tail -> head + "d" :: suffixAll(tail)

let recursive scaleBy factor xs =
    match xs with
        | [] -> []
        | head :: tail -> head * factor :: scaleBy(factor)(tail)

let recursive keepEven xs =
    match xs with
        | [] -> []
        | head :: tail ->
            if head % 2 == 0
            then head :: keepEven(tail)
            else keepEven(tail)

let recursive appendTo right xs =
    match xs with
        | [] -> right
        | head :: tail -> head :: appendTo(right)(tail)

let recursive traceAll xs =
    match xs with
        | [] -> []
        | head :: tail ->
            let _ = io.write(text.fromInt(head) + ",")
            in head :: traceAll(tail)

let recursive sum xs acc =
    match xs with
        | [] -> acc
        | head :: tail -> sum(tail)(acc + head)

let recursive render xs =
    match xs with
        | [] -> ""
        | head :: [] -> text.fromInt(head)
        | head :: tail -> text.fromInt(head) + "," + render(tail)

let recursive renderText xs =
    match xs with
        | [] -> ""
        | head :: [] -> head
        | head :: tail -> head + "-" + renderText(tail)

let show xs = "[" + render(xs) + "]"

// A captured callback: the multiplier is captured from the enclosing binding, not a parameter of the
// recursive function, so the transformed loop must keep the environment reachable across iterations.
let scaledByThree =
    (let factor = 10
    in
        let recursive go xs =
            match xs with
                | [] -> []
                | head :: tail -> head * factor + 1 :: go(tail)
        in go)

// The produced list is consumed twice, so the spine must be a genuinely owned value rather than
// something the producer may reuse or reclaim after the first read.
let shared = incAll(10 :: 20 :: 30 :: [])

io.print(show(incAll([])) + "|" + show(incAll(6 :: [])) + "|" + show(incAll(1 :: 2 :: 3 :: [])) + "|" + renderText(suffixAll("ab" :: "bc" :: "cd" :: [])) + "|" + show(scaledByThree(1 :: 2 :: 3 :: [])) + "|" + text.fromInt(sum(shared)(0)) + "|" + text.fromInt(sum(shared)(0)) + "|" + text.fromInt(sum(traceAll(1 :: 2 :: 3 :: []))(0)) + "|" + show(keepEven(1 :: 2 :: 3 :: 4 :: [])) + "|" + show(appendTo(9 :: 8 :: [])(1 :: 2 :: [])))
