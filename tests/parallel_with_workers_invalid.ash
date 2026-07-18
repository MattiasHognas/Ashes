// exit: 1
// expect: Ashes.Task.Parallel.withWorkers: worker count must be positive.
import Ashes.Task.Parallel
import Ashes.IO
Ashes.IO.print(Ashes.Task.Parallel.withWorkers(0)(given (_u) -> 42))
