// expect-compile-error: 'provide' declarations (static capability satisfaction) are not yet implemented
capability Ord =
    | compare : Int -> Int -> Int

provide Ord =
    | compare = given (a) -> given (b) -> a - b

Ashes.IO.print(0)
