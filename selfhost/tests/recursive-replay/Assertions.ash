let check (label: Str) pinned pending mode expectedError =
    label
    |> Ashes.IO.print
    |> (given (_) ->
        match lowerRecursiveMembersPinningSiblingResults([])([])([])(0)(memberLower)(CoreLoweringState(pinned = pinned, pending = pending, siblings = [], proven = 0, mode = mode)) with
            | LoweredCoreValue { state = finalState, error = error } ->
                if finalState.pinned == pinned && error == expectedError
                then Ashes.IO.print("passed")
                else Ashes.IO.panic("recursive replay lost pinned boundary"))

Unit
|> (given (_) -> check("pinned-empty")(true)([])(0)(None))
|> (given (_) -> check("unpinned-empty")(false)([])(0)(None))
|> (given (_) -> check("pinned-new-goal")(true)([])(1)(Some(1)))
|> (given (_) -> check("unpinned-new-goal")(false)([])(1)(None))
|> (given (_) -> check("pinned-prior-goal")(true)([9])(0)(None))
|> (given (_) -> check("pinned-settled-sibling")(true)([])(2)(None))
|> (given (_) -> check("unpinned-settled-sibling")(false)([])(2)(None))
