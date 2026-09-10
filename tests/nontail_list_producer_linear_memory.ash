// expect: 140000
// A non-tail recursive list producer builds one cell per level, and each level's call site must
// keep the level below alive past its own arena window. With the spine in the arena that meant
// copying the whole result out at every level, so peak memory was quadratic in the list's length:
// 70 MB at 2000 elements, 1010 MB at 8000, 6.26 GB at the 20000 this test builds. The spine is
// reference-counted instead, so the call site's conditional copy-out is skipped and the cells are
// allocated once. This test is a memory regression: it completes in a few megabytes and its
// running time is linear, where the old shape would exhaust a small machine before finishing.

let recursive makeList (count: Int) =
    if count == 0
    then []
    else 7 :: makeList(count - 1)

let recursive sumList (values: List(Int)) (total: Int) =
    match values with
        | [] -> total
        | value :: rest -> sumList(rest)(total + value)

20000
|> makeList
|> sumList
|> (given (f) -> f(0))
|> Ashes.Text.fromInt
|> Ashes.IO.print
