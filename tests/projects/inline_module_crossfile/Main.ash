// expect: 30
import Geom.Vec
import Ashes.IO
6
|> Vec.add(4)
|> Vec.scale(3)
|> Ashes.IO.print
