// expect: 5 3 14 1 6
// Borrowed Bytes views must be materialized before an RC closure, ADT, tuple, or list owns them.
// Fresh owned buffers and their function-forwarded results may be stored directly. The final list
// case crosses a TCO back edge, which used to reject every element type containing Bytes.
type ByteBox =
    | ByteBox(Bytes)

let makeClosure text =
    (let bytes = Ashes.Byte.fromText(text)
    in
        given (_unit) -> Ashes.Byte.length(bytes))

let makeBox text = ByteBox(Ashes.Byte.fromText(text))

let makeTuple text = (Ashes.Byte.fromText(text), 9)

let owned byte = Ashes.Byte.singleton(byte)

let forwarded byte = owned(byte)

let recursive build n acc =
    if n <= 0
    then acc
    else build(n - 1)(Ashes.Byte.fromText("xy") :: acc)

let recursive totalLength values total =
    match values with
        | [] -> total
        | bytes :: rest -> totalLength(rest)(total + Ashes.Byte.length(bytes))

let closureLength = makeClosure("hello")(0)

let boxLength =
    match makeBox("box") with
        | ByteBox(bytes) -> Ashes.Byte.length(bytes)

let tupleLength =
    match makeTuple("tuple") with
        | (bytes, value) -> Ashes.Byte.length(bytes) + value

let forwardedLength = Ashes.Byte.length(forwarded(1u8))

let listLength = totalLength(build(3)([]))(0)

Ashes.IO.print(Ashes.Text.fromInt(closureLength) + " " + Ashes.Text.fromInt(boxLength) + " " + Ashes.Text.fromInt(tupleLength) + " " + Ashes.Text.fromInt(forwardedLength) + " " + Ashes.Text.fromInt(listLength))
