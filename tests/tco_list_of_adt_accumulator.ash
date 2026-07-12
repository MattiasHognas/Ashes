// Regression: a TCO loop threading a List(fixed-shape-ADT) accumulator (n-body's List(Body) shape)
// takes the DeepAdt copy-out (synthesized recursive list copier) and the FIXED loop-entry watermark,
// so the loop runs in constant memory instead of growing O(N). Also guards the two-pass copy-out's
// disjointness: the DeepAdt up-copy is cloned twice so the post-reset down-copy can never overlap its
// source, no matter how little the loop body allocated (the readme_showcase 41.00-vs-12.50 miscompile:
// an overlapping, skewed down-copy read its own partially-written output). The test runner compiles
// UNOPTIMIZED IR, which is exactly the path where the overlap bug manifested.
// expect: 3010.0
import Ashes.IO as io
import Ashes.Text as text
type Body =
    | pos: Float
    | vel: Float

let recursive advance bodies dt n =
    if n <= 0
    then bodies
    else
        let recursive step bs =
            match bs with
                | [] -> []
                | b :: rest ->
                    match b with
                        | Body(p, v) -> Body(pos = p + v * dt, vel = v) :: step(rest)
        in advance(step(bodies))(dt)(n - 1)

let recursive sumPos bs acc =
    match bs with
        | [] -> acc
        | b :: rest ->
            match b with
                | Body(p, _v) -> sumPos(rest)(p + acc)

let start = [Body(pos = 0.0, vel = 1.0), Body(pos = 10.0, vel = 2.0)]

io.print(text.formatFloat(sumPos(advance(start)(1.0)(1000))(0.0))(1))
