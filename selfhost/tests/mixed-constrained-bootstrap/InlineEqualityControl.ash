type Value =
    | One
    | Two(Int)
    deriving {Eq}

let list =
    ((given (expected) ->
        given (actual) ->
            if expected == actual
            then Unit
            else Ashes.IO.panic("inline list failed")))([1])([1])

let listDone = Ashes.IO.print("inline list passed")

let boolean =
    ((given (expected) ->
        given (actual) ->
            if expected == actual
            then Unit
            else Ashes.IO.panic("inline Bool failed")))(true)(true)

let boolDone = Ashes.IO.print("inline Bool passed")

let custom =
    ((given (expected) ->
        given (actual) ->
            if expected == actual
            then Unit
            else Ashes.IO.panic("inline ADT failed")))([One, Two(2)])([One, Two(2)])

Ashes.IO.print("inline ADT passed")
