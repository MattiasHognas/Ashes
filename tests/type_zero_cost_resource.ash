// file: zero_cost_resource.txt = hello
// expect: hello

import Ashes.IO.File
type WrappedHandle = WrappedHandle(FileHandle)

let opened = Ashes.IO.File.open("zero_cost_resource.txt")

match opened with
    | Error(_) -> Ashes.IO.print("error")
    | Ok(fileHandle) ->
        let wrapped = WrappedHandle(fileHandle)
        in
            match wrapped with
                | WrappedHandle(unwrapped) ->
                    match Ashes.IO.File.readChunk(unwrapped)(5) with
                        | Error(_) -> Ashes.IO.print("read-error")
                        | Ok(text) -> Ashes.IO.print(text)
