// expect: 3/4
import Helper

module Local =
    let twice xs = Ashes.Collection.List.length(xs) * 2

Ashes.IO.print(Helper.describe([1, 2, 3]) + "/" + Ashes.Text.fromInt(Local.twice([1, 2])))
