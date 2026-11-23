// expect-compile-error: ASH048 Constructor 'Less' is declared in module 'Ashes.Trait', which this module does not import.
Ashes.IO.print(match Ord.compare(1)(2) with
    | Less -> "less"
    | _ -> "other")
