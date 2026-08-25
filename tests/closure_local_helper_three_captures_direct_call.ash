// expect: 40000000
let recursive loop =
    given (n: Int) ->
        given (acc: Int) ->
            if n <= 0
            then acc
            else
                let a = n * 2
                in
                    let b = acc + 1
                    in
                        let c = a + 1
                        in
                            let step =
                                given (x: Int) -> x + a + b + c
                            in loop(n - 1)(step(1) - a - c)

Ashes.IO.print(loop(20000000)(0))
