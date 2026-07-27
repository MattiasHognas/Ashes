// Regression: a freshly constructed match scrutinee (Continue/Done) whose one heap-typed field is
// extracted by name and forwarded, unmodified, as the self-recursive tail call's own next-iteration
// argument -- the exact fannkuch-redux Step/State shape. The wrapper cell itself is a separate
// allocation from the extracted field; forwarding the field into the back edge must not silently skip
// releasing the wrapper's own header. This is a leak, not a correctness bug the exit value would
// reveal, so this test's job is functional correctness (right sum, no crash) across many iterations;
// the wrapper-drop fix itself is pinned precisely by MatchScrutineeWrapperDropTests (C#, IR-level) and
// by peak-RSS measurement on challenges/fannkuch-redux directly.
// expect: 5000050000
import Ashes.IO as io
import Ashes.Text as text
type Pair =
    | S(List(Int), Int)

type Step =
    | Done
    | Continue(Pair, Int)

let recursive nextStep n st =
    if n <= 0
    then Done
    else
        match st with
            | S(xs, c) -> Continue(S(xs)(c + 1))(n)

let recursive loop n st acc =
    match nextStep(n)(st) with
        | Done -> acc
        | Continue(st2, r2) -> loop(n - 1)(st2)(acc + r2)

let result = loop(100000)(S([1, 2, 3])(0))(0)

io.print(text.fromInt(result))
