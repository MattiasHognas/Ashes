// expect: ok
import Ashes.Rpc
let hasLength =
    match Ashes.Rpc.parseContentLength("Content-Length: 42") with
        | None -> false
        | Some(n) -> n == 42
in
    let noHeader =
        match Ashes.Rpc.parseContentLength("X-Other: ignored") with
            | None -> true
            | Some(_) -> false
    in
        let zeroLength =
            match Ashes.Rpc.parseContentLength("Content-Length: 0") with
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
