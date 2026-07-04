// expect: 1720000000
capability Clock =
    | now : Unit -> Int

provide Clock =
    | now = 
        given (_) -> 1720000000

let stamp = 
    given (_) -> Clock.now(Unit)

Ashes.IO.print(stamp(Unit))
