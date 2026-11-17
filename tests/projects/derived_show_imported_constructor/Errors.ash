export (
    type LookupError(..),
)

type LookupError =
    | Missing(Str)
    | Invalid(Int, Str)
    | Empty
    deriving {Show}
