// expect: 0
// exit: 0

let result : List(Int) =
    let capturedEmpty =
        if false
        then []
        else []
    in
        let recursive countDown : Int -> List(Int) =
            given (count: Int) ->
                if count <= 0
                then capturedEmpty
                else countDown(count - 1)
        in countDown(2)
in
    result
    |> Ashes.Collection.List.length
    |> Ashes.Text.fromInt
    |> Ashes.IO.print
