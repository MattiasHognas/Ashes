// expect-compile-error: 'effect' has been renamed to 'capability'
effect Clock =
    | now : Unit -> Int

Ashes.IO.print(0)
