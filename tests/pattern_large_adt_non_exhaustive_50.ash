// expect-compile-error: Non-exhaustive match expression. Missing constructor(s): 'Inst01', 'Inst02', 'Inst03', ... and 45 more.
type BigInst =
    | Inst00
    | Inst01
    | Inst02
    | Inst03
    | Inst04
    | Inst05
    | Inst06
    | Inst07
    | Inst08
    | Inst09
    | Inst10
    | Inst11
    | Inst12
    | Inst13
    | Inst14
    | Inst15
    | Inst16
    | Inst17
    | Inst18
    | Inst19
    | Inst20
    | Inst21
    | Inst22
    | Inst23
    | Inst24
    | Inst25
    | Inst26
    | Inst27
    | Inst28
    | Inst29
    | Inst30
    | Inst31
    | Inst32
    | Inst33
    | Inst34
    | Inst35
    | Inst36
    | Inst37
    | Inst38
    | Inst39
    | Inst40
    | Inst41
    | Inst42
    | Inst43
    | Inst44
    | Inst45
    | Inst46
    | Inst47
    | Inst48
    | Inst49

let instToInt =
    given (i) ->
        match i with
            | Inst00 -> 0
            | Inst04 -> 4
in Ashes.IO.print(instToInt(Inst00))
