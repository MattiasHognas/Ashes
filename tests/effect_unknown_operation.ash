// expect-compile-error: Effect 'Clock' has no operation 'tomorrow'

effect Clock =
    | now : Unit -> Int

let t = Clock.tomorrow(Unit)

Ashes.IO.print(t)
