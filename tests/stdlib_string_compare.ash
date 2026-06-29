// expect: 0,-1,1,-1,1,-1
import Ashes.String
import Ashes.Text
import Ashes.IO
let parts = Ashes.Text.fromInt(Ashes.String.compare("apple")("apple")) + "," + Ashes.Text.fromInt(Ashes.String.compare("apple")("banana")) + "," + Ashes.Text.fromInt(Ashes.String.compare("banana")("apple")) + "," + Ashes.Text.fromInt(Ashes.String.compare("app")("apple")) + "," + Ashes.Text.fromInt(Ashes.String.compare("apple")("app")) + "," + Ashes.Text.fromInt(Ashes.String.compare("Zurich")("Zürich"))
in Ashes.IO.print(parts)
