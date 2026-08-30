// expect: -9223372036854775808
let shifted = 1 << 63

let subtracted = -9223372036854775807 - 1

Ashes.IO.print(if shifted == subtracted
then shifted
else 0)
