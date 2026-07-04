// expect: 2
import Ashes.IO as io
import Ashes.List as list
import Ashes.Async as task
type Shape =
    | Circle(Float)
    | Rect(Float, Float)

let area s = 
    match s with
        | Circle(r) -> 3.14159 * r * r
        | Rect(w, h) -> w * h
in 
    let shapes = [Circle(5.0), Rect(3.0)(4.0), Circle(1.0)]
    in 
        let t = 
            async(let count = 
                shapes
                |> list.map(area)
                |> list.filter(given (a) -> a >= 10.0)
                |> list.length
            in count)
        in 
            match task.run(t) with
                | Ok(n) when n >= 1 -> io.print(n)
                | Ok(_) -> io.print(0)
                | Error(_) -> io.print(0)
