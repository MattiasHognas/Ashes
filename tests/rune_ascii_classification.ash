// expect: true|true|true|false
Ashes.IO.print((if Ashes.Rune.isAsciiLetter('z')
then "true"
else "false") + "|" + (if Ashes.Rune.isAsciiDigit('7')
then "true"
else "false") + "|" + (if Ashes.Rune.isAsciiWhiteSpace('\n')
then "true"
else "false") + "|" + (if Ashes.Rune.isAsciiLetter('é')
then "true"
else "false"))
