// expect: -42|-12.25|0xbeef
Ashes.IO.print(Ashes.Text.fromInt(-42) + "|" + Ashes.Text.fromFloat(0.0 - 12.25) + "|" + Ashes.Text.toHex(48879))
