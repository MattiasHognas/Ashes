// expect: 3
// Ashes.Task.Parallel names its own `both` and `withWorkers` by their full path; a project build
// must not read that as the module importing itself.
import Ashes.Task.Parallel
[1, 2, 3]
|> Ashes.Task.Parallel.mapWithWorkers(2)(given (x) -> x + 1)
|> Ashes.Collection.List.length
|> Ashes.IO.print
