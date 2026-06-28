import Ashes.List
let add acc x = acc + x
in Ashes.IO.print(List.fold(add)(0)([1, 2, 3, 4]))
