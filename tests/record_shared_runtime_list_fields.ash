// expect: left_0|left_0|right_0|right_0

type DerivedNames =
    | copied: List(Str)
    | original: List(Str)

let recursive copyNames names =
    match names with
        | [] -> []
        | head :: tail -> head :: copyNames(tail)

let recursive generatedFieldNames prefix count index =
    if index >= count
    then []
    else prefix + "_" + Ashes.Text.fromInt(index) :: generatedFieldNames(prefix)(count)(index + 1)

let derivedNames prefix =
    (let fields = generatedFieldNames(prefix)(1)(0)
    in DerivedNames(copied = copyNames(fields), original = fields))

let left = derivedNames("left")

let right = derivedNames("right")

match (left, right) with
    | (DerivedNames { copied = leftCopied :: [], original = leftOriginal :: [] }, DerivedNames { copied = rightCopied :: [], original = rightOriginal :: [] }) -> Ashes.IO.print(leftCopied + "|" + leftOriginal + "|" + rightCopied + "|" + rightOriginal)
    | _ -> Ashes.IO.print("shape-corrupted")
