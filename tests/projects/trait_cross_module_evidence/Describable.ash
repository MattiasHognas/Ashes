trait Describable(a) =
    | describe : a -> Str
    | describeAll : a -> a -> Str =
        given (left) ->
            given (right) -> Describable.describe(left) + "," + Describable.describe(right)
