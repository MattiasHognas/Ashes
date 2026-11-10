let check actual =
    if actual
    then Unit
    else Ashes.IO.panic("worklist differential mismatch")

let sameAnswer root path state = originalGeneralRcAdmissible(root)(path)(state) == queryGeneralRcAdmissibility(root)(path)(state)

let recursive checkRoots graph mask root =
    if root == 3
    then Unit
    else
        let state = CoreLoweringState(graph = graph, forbidden = mask, mode = 0, cached = None)
        in
            state
            |> sameAnswer(node(root))([])
            |> check
            |> (given (_) -> checkRoots(graph)(mask)(root + 1))

let recursive checkMasks graph mask =
    if mask == 8
    then Unit
    else
        0
        |> checkRoots(graph)(mask)
        |> (given (_) -> checkMasks(graph)(mask + 1))

let recursive checkGraphs graph =
    if graph == 512
    then Unit
    else
        0
        |> checkMasks(graph)
        |> (given (_) -> checkGraphs(graph + 1))

let checkSpecial unit =
    (let collision = CoreLoweringState(graph = 0, forbidden = 2, mode = 1, cached = None)
    in
        let left = SemNamed(0)("Shared")([])
        in
            let right = SemNamed(1)("Shared")([])
            in
                let siblings = SemTuple([left, right])
                in
                    !queryGeneralRcAdmissibility(siblings)([])(collision)
                    |> check
                    |> (given (_) -> check(searchGeneralRcAdmissibility([VisitAdmissibilityType(siblings)(true)])([])(collision) == None))
                    |> (given (_) ->
                        collision
                        |> sameAnswer(siblings)([])
                        |> check)
                    |> (given (_) ->
                        (collision with mode = 2)
                        |> sameAnswer(left)([])
                        |> check)
                    |> (given (_) ->
                        (collision with mode = 2)
                        |> queryGeneralRcAdmissibility(left)([])
                        |> check)
                    |> (given (_) ->
                        collision
                        |> sameAnswer(SemNamed(5)("Task")([]))([SemNamed(5)("Task")([])])
                        |> check)
                    |> (given (_) ->
                        (collision with cached = Some((false, false)))
                        |> sameAnswer(SemNamed(90)("Scalar")([]))([])
                        |> check)
                    |> (given (_) ->
                        (collision with forbidden = 0, mode = 3)
                        |> sameAnswer(node(10))([])
                        |> check)
                    |> (given (_) ->
                        (collision with forbidden = 0, mode = 3)
                        |> queryGeneralRcAdmissibility(node(26))([])
                        |> check))

Unit
|> checkSpecial
|> (given (_) -> checkGraphs(0))
|> (given (_) -> Ashes.IO.print("12288 graphs and special cases ok"))
