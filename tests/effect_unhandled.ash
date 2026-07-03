// expect-compile-error: Unhandled effect 'Clock'

effect Clock =
    | now : Unit -> Int

let t = Clock.now(Unit)

Ashes.IO.print(t)
