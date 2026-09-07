// expect-compile-error: must be used inside an async task
1
|> async
|> Ashes.Task.fork
