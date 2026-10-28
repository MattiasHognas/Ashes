// One arm passes the accumulator through unchanged while the other conses a head tuple holding a
// nested tuple over the pattern-bound label; the back edge copies the new cell with its nested
// tuple, which borrows the label like the head tuple does.
// expect: 3 lambda_10:7 lambda_3:0 lambda_5:4
let recursive resolveGroups (groups: List((Str, Int))) (known: List(((Str, Int), Int))) =
    match groups with
        | [] -> known
        | (label, index) :: tail ->
            if index == 1
            then resolveGroups(tail)(known)
            else resolveGroups(tail)(((label, index), index + 2) :: known)

let describe (entries: List(((Str, Int), Int))) =
    Ashes.Collection.List.foldLeft(given (text) ->
        given (entry) ->
            match entry with
                | ((label, index), _) -> text + " " + label + ":" + Ashes.Text.fromInt(index))("")(entries)

let known = resolveGroups([("lambda_5", 4), ("lambda_3", 1), ("lambda_3", 0), ("lambda_10", 7)])([])

Ashes.IO.print(Ashes.Text.fromInt(Ashes.Collection.List.length(known)) + describe(known))
