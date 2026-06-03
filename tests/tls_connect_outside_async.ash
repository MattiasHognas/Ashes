// tls-server: accept
// expect: ok
let sock = await Ashes.Net.Tls.connect("localhost")(__TCP_PORT__)
in
    let _ = await Ashes.Net.Tls.close(sock)
    in Ashes.IO.print("ok")
