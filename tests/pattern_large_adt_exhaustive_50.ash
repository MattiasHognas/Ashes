// expect: 25
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

let instToInt i =
    match i with
        | Inst00 -> 0
        | Inst01 -> 1
        | Inst02 -> 2
        | Inst03 -> 3
        | Inst04 -> 4
        | Inst05 -> 5
        | Inst06 -> 6
        | Inst07 -> 7
        | Inst08 -> 8
        | Inst09 -> 9
        | Inst10 -> 10
        | Inst11 -> 11
        | Inst12 -> 12
        | Inst13 -> 13
        | Inst14 -> 14
        | Inst15 -> 15
        | Inst16 -> 16
        | Inst17 -> 17
        | Inst18 -> 18
        | Inst19 -> 19
        | Inst20 -> 20
        | Inst21 -> 21
        | Inst22 -> 22
        | Inst23 -> 23
        | Inst24 -> 24
        | Inst25 -> 25
        | Inst26 -> 26
        | Inst27 -> 27
        | Inst28 -> 28
        | Inst29 -> 29
        | Inst30 -> 30
        | Inst31 -> 31
        | Inst32 -> 32
        | Inst33 -> 33
        | Inst34 -> 34
        | Inst35 -> 35
        | Inst36 -> 36
        | Inst37 -> 37
        | Inst38 -> 38
        | Inst39 -> 39
        | Inst40 -> 40
        | Inst41 -> 41
        | Inst42 -> 42
        | Inst43 -> 43
        | Inst44 -> 44
        | Inst45 -> 45
        | Inst46 -> 46
        | Inst47 -> 47
        | Inst48 -> 48
        | Inst49 -> 49
in Ashes.IO.print(instToInt(Inst25))
