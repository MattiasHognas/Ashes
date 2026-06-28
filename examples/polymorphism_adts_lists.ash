// Two polymorphic helpers as flat top-level `let`s, then a sequence of
// top-level uses at different types, ending in a trailing expression.

let firstOr = 
    fun (xs) -> 
        fun (def) -> 
            match xs with
                | [] -> def
                | x :: _ -> x

let unwrapOr = 
    fun (opt) -> 
        fun (def) -> 
            match opt with
                | None -> def
                | Some(x) -> x

let _a = firstOr([1, 2, 3])(0)

let _b = firstOr(["a", "b"])("z")

let _c = unwrapOr(Some(10))(0)

let _d = unwrapOr(None)("fallback")

let _r1 = Ok(firstOr([4, 5])(0))

let _r2 = Error(firstOr([])(0))
in Ashes.IO.print("ok")
