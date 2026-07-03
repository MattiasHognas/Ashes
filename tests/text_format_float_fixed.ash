// expect: 3.141592654|1.500000000|0.100000000|100.000000000|-12.250|2.000|-0.169075164|1.000000000|3|2|0.500000000000000000
let nineDp = Ashes.Text.formatFloat(3.141592653589793)(9) + "|" + Ashes.Text.formatFloat(1.5)(9) + "|" + Ashes.Text.formatFloat(0.1)(9) + "|" + Ashes.Text.formatFloat(100.0)(9)

let negatives = Ashes.Text.formatFloat(0.0 - 12.25)(3) + "|" + Ashes.Text.formatFloat(2.0)(3) + "|" + Ashes.Text.formatFloat(0.0 - 0.169075164)(9)

let edges = Ashes.Text.formatFloat(0.9999999996)(9) + "|" + Ashes.Text.formatFloat(2.5)(0) + "|" + Ashes.Text.formatFloat(1.5)(-3) + "|" + Ashes.Text.formatFloat(0.5)(25)

Ashes.IO.print(nineDp + "|" + negatives + "|" + edges)
