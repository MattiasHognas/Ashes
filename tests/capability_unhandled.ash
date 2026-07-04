// expect-compile-error: Unhandled capability 'Clock'

capability Clock =
    | now : Unit -> Int

let t = Clock.now(Unit)

Ashes.IO.print(t)
