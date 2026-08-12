// A completed spawned task may borrow a shared list while the parent retains and observes the same
// list. Driving the scheduler completes and reaps the detached task before the parent renders its
// alias; the ignored let chain must transfer that alias instead of dropping it at the inner scope.
// expect: [3]
import Ashes.IO
import Ashes.Task
import Ashes.Text
Ashes.IO.print(let recursive render : List(Int) -> Str =
    given (items: List(Int)) ->
        match items with
            | [] -> "]"
            | head :: tail ->
                Ashes.Text.fromInt(head) + (match tail with
                    | [] -> "]"
                    | _ -> "," + render(tail))
in
    "[" + render(let shared =
        let head = 3
        in
            let tail = []
            in head :: tail
    in
        let _spawned = Ashes.Task.spawn(async(shared))
        in
            let _driven =
                Ashes.Task.run(async(match await Ashes.Task.sleep(0) with
                    | Ok(_value) -> 0
                    | Error(_error) -> 0))
            in shared))
