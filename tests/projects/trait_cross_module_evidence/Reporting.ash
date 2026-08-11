import Describable
let recursive report : Str -> List(a) -> Str requires {Describable(a)} =
    given (prefix) ->
        given (items) ->
            match items with
                | [] -> ""
                | h :: t -> prefix + ":" + Describable.describe(h) + " " + report(prefix)(t)

let bothEqual : a -> b -> Bool requires {Eq(a), Eq(b)} =
    given (left) ->
        given (right) ->
            if left == left
            then Eq.equal(right)(right)
            else false
