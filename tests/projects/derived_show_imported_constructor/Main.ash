// expect: Missing("key")|Invalid(3, "bad")|Empty
import Errors
Ashes.IO.print(Show.show(Errors.Missing("key")) + "|" + Show.show(Errors.Invalid(3)("bad")) + "|" + Show.show(Errors.Empty))
