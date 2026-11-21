// expect: less
import Ashes.Trait
Ashes.IO.print(match Ord.compare(1)(2) with
    | Less -> "less"
    | _ -> "other")
