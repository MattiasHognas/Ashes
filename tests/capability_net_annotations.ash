// expect: ok

let fetch : Str -> Task(Str, Str) needs {NetConnect} = 
    given (u) -> Ashes.Http.get(u)

let opener : Int -> Task(Str, Socket) needs {NetListen} = 
    given (p) -> Ashes.Net.Tcp.Server.listen(p)

Ashes.IO.print("ok")
