// tcp-server: accept
// expect: ok
let sock = await Ashes.Net.Tcp.connect("127.0.0.1")(__TCP_PORT__)
in
    let _ = await Ashes.Net.Tcp.close(sock)
    in Ashes.IO.print("ok")
