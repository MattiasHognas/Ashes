// Flat top-level style: a file is a sequence of top-level `let` declarations
// (no trailing `in`), each visible to the declarations that follow it, ending
// in a single trailing expression. Compare the nested `let ... in` pyramid this
// program used to be -- the bindings now read top to bottom instead of nesting.

let add = 
    fun (x) -> 
        fun (y) -> x + y

let addTen = add(10)

let result = addTen(32)
in Ashes.IO.print(result)
