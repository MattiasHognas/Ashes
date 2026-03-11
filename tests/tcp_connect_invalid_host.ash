// expect: error
match Ashes.Net.Tcp.connect("not-a-host")(80) with
    | Ok(_) -> Ashes.IO.print("fail")
    | Error(_) -> Ashes.IO.print("error")
