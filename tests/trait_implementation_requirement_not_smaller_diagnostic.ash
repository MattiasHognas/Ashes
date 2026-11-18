// A generic implementation whose requirement is larger than its head could recurse forever.
// expect-compile-error: ASH025 Implementation requirement 'Grow(List<List<a>>)' must be structurally smaller than head 'Grow(List<a>)'.
trait Grow(a) =
    | grow : a -> a

implement Grow(List(a)) requires {Grow(List(List(a)))} =
    | grow =
        given (x) -> x

Ashes.IO.print(1)
