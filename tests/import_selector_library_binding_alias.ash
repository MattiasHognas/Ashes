// expect: 2

// A binding selector of a library module under an alias, in a single-file program: the alias is
// bound in the entry like any selected name, in declarations and the trailing expression alike.
import Ashes.Collection.List.reverse as reverseList
let words = reverseList(["a", "b"])

let finish items = items |> reverseList

words
|> finish
|> Ashes.Collection.List.length
|> Ashes.IO.print
