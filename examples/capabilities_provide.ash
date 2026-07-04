// Static capability providers: dependency injection with no handler and no framework.
// `Clock` and `Rng` are satisfied by top-level `provide` declarations, resolved at compile time.
import Ashes.IO
import Ashes.Text
capability Clock =
    | now : Unit -> Int

capability Rng =
    | next : Unit -> Int

provide Clock =
    | now = 
        given (_) -> 1720000000

provide Rng =
    | next = 
        given (_) -> 42

let report = 
    given (_) -> 
        let t = Clock.now(Unit)
        in 
            let r = Rng.next(Unit)
            in "t=" + Ashes.Text.fromInt(t) + " r=" + Ashes.Text.fromInt(r)

Ashes.IO.print(report(Unit))
