// expect: /
import Ashes.IO.Path
let recursive rootOf path =
    match parent(Unix)(path) with
        | parentPath ->
            if parentPath == path
            then path
            else rootOf(parentPath)

"/tmp/ashes/project"
|> rootOf
|> Ashes.IO.print
