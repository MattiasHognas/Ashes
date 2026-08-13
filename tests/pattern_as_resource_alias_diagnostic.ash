// expect-compile-error: Resource-bearing pattern alias 'whole' cannot coexist with a nested resource-bearing binding.
import Ashes.IO.File
type Bag =
    | Empty
    | Item(FileHandle)

let inspect bag =
    match bag with
        | Item(file) as whole -> 1
        | Empty -> 0
in inspect
