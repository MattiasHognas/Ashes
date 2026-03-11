// expect-compile-error: Import module qualifier collision for 'X'
import A.X
import B.X
Ashes.IO.print(X.z)
