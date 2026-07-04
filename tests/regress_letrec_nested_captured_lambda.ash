// Regression: a top-level `let recursive` whose body is a nested NON-recursive
// `let`-bound lambda that captures an outer parameter (`n`), combined with a
// self-recursive tail call, used to crash LLVM codegen with
//   KeyNotFoundException: The given key 'lambda_2_body' was not present in the dictionary
// (LlvmCodegen.GetLabelBlock / EmitJump). The nested `helper` lambda was wrongly
// treated as the recursive chain's innermost TCO lambda, so its loop label was
// emitted into helper's frame while the outer self-call jumped to it.
//
// total(4)(0) = 4 + 3 + 2 + 1 + 0 = 10
// expect: 10
let recursive total n acc = 
    (let helper x = x + n
    in 
        if n == 0
        then acc
        else total(n - 1)(helper(acc)))

Ashes.IO.print(total(4)(0))
