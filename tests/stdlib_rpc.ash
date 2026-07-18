// expect: ok
import Ashes.Net.Rpc
let hasLength =
    match Ashes.Net.Rpc.parseContentLength("Content-Length: 42") with
        | None -> false
        | Some(n) -> n == 42
in
    let noHeader =
        match Ashes.Net.Rpc.parseContentLength("X-Other: ignored") with
            | None -> true
            | Some(_) -> false
    in
        let zeroLength =
            match Ashes.Net.Rpc.parseContentLength("Content-Length: 0") with
                | None -> false
                | Some(n) -> n == 0
        in
            if hasLength
            then
                if noHeader
                then
                    if zeroLength
                    then Ashes.IO.print("ok")
                    else Ashes.IO.print("fail")
                else Ashes.IO.print("fail")
            else Ashes.IO.print("fail")
