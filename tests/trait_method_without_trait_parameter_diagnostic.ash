// expect-compile-error: ASH010 Method 'version' of trait
trait Named(a) =
    | label : a -> Str
    | version : Int

Ashes.IO.print(1)
