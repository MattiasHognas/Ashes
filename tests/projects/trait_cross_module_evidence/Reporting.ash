import Describable
let recursive report : Str -> List(a) -> Str requires {Describable(a)} =
    given (prefix) ->
        given (items) ->
            match items with
                | [] -> ""
                | h :: t -> prefix + ":" + Describable.describe(h) + " " + report(prefix)(t)
