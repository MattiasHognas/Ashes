// An operator over an annotated lambda parameter in the trailing expression takes the annotation's
// type and needs no use to settle it.
// expect: 1
[given (x: Int) -> x + x]
|> Ashes.Collection.List.length
|> Ashes.IO.print
