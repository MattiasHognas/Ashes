// expect-compile-error: Capability 'Clock' has no operation 'tomorrow'

capability Clock =
    | now : Unit -> Int

let t = Clock.tomorrow(Unit)

Ashes.IO.print(t)
