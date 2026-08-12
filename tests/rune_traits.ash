// expect: true|true|😀|128512
import Ashes.Trait
Ashes.IO.print((if Eq.equal('a')('a')
then "true"
else "false") + "|" + (if Ord.less('a')('b')
then "true"
else "false") + "|" + Show.show('😀') + "|" + Ashes.Text.fromInt(Hash.hash('😀')))
