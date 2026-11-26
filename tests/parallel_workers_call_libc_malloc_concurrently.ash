// expect: 8|8000000|800000
// Eight workers, forked by `map` and by the queue `reduce` runs, allocate and free through libc at
// the same time. A program that loads libc starts its workers as libc threads, each with its own
// malloc state; workers sharing the starting thread's state corrupt the heap and abort.
import Ashes.Task.Parallel
external libcMalloc(Int) -> Int needs {} = "malloc@libc.so.6"
external libcFree(Int) -> void needs {} = "free@libc.so.6"

let recursive churn (seed: Int) (left: Int) (count: Int) =
    if left == 0
    then count
    else
        let first = libcMalloc(16 + (seed + left) % 200)
        in
            let second = libcMalloc(24 + (seed * 3 + left) % 300)
            in
                let _ = libcFree(first)
                in
                    let _ = libcFree(second)
                    in
                        churn(seed)(left - 1)(count + (if first != 0 && second != 0
                        then 1
                        else 0))

let counts =
    Ashes.Task.Parallel.map(given (seed: Int) -> churn(seed)(1000000)(0))([1, 2, 3, 4, 5, 6, 7, 8])

let recursive total xs acc =
    match xs with
        | [] -> acc
        | x :: rest -> total(rest)(acc + x)

let recursive len xs acc =
    match xs with
        | [] -> acc
        | _ :: rest -> len(rest)(acc + 1)

let reduced =
    Ashes.Task.Parallel.reduce(given (a: Int) ->
        given (b: Int) -> a + b)(0)(given (seed: Int) -> churn(seed)(100000)(0))([1, 2, 3, 4, 5, 6, 7, 8])

Ashes.IO.print(Ashes.Text.fromInt(len(counts)(0)) + "|" + Ashes.Text.fromInt(total(counts)(0)) + "|" + Ashes.Text.fromInt(reduced))
