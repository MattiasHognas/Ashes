// expect-compile-error: 'uses' has been renamed to 'needs'
capability Clock =
    | now : Unit -> Int

let f : Unit -> Int uses {Clock} = given (_) -> perform Clock.now(Unit)

Ashes.IO.print(0)
