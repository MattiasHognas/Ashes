// exit: 1
// expect: Ashes.Parallel.withWorkers: worker count must be positive.
import Ashes.Parallel
import Ashes.IO
Ashes.IO.print(Ashes.Parallel.withWorkers(0)(given (_u) -> 42))
