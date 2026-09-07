// exit: 1
// expect: Ashes.Task.Parallel.withWorkers: worker count must be positive.
import Ashes.Task.Parallel
import Ashes.IO
(given (_u) -> 42)
|> Ashes.Task.Parallel.withWorkers(0)
|> Ashes.IO.print
