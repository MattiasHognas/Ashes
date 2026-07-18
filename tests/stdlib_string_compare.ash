// expect: 0,-1,1,-1,1,-1
import Ashes.Text
import Ashes.Text
import Ashes.IO
let parts = Ashes.Text.fromInt(Ashes.Text.compare("apple")("apple")) + "," + Ashes.Text.fromInt(Ashes.Text.compare("apple")("banana")) + "," + Ashes.Text.fromInt(Ashes.Text.compare("banana")("apple")) + "," + Ashes.Text.fromInt(Ashes.Text.compare("app")("apple")) + "," + Ashes.Text.fromInt(Ashes.Text.compare("apple")("app")) + "," + Ashes.Text.fromInt(Ashes.Text.compare("Zurich")("Zürich"))
in Ashes.IO.print(parts)
