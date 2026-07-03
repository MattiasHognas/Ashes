// Ashes.Math — hermetic integer core (Layer 1).
// Pure Ashes: no native payload, no new intrinsics. Float and transcendental
// layers (sqrt, toFloat, sin, ...) are added separately.

let abs n = 
    if n < 0
    then -n
    else n

let signum n = 
    if n < 0
    then -1
    else 
        if n > 0
        then 1
        else 0

let min a b = 
    if a < b
    then a
    else b

let max a b = 
    if a > b
    then a
    else b

let clamp lo hi n = 
    if n < lo
    then lo
    else 
        if n > hi
        then hi
        else n

let remOf a b = a - a / b * b

let rec gcdGo a b = 
    if b == 0
    then a
    else gcdGo(b)(remOf(a)(b))

let gcd a b = gcdGo(abs(a))(abs(b))

let lcm a b = 
    if a == 0
    then 0
    else 
        if b == 0
        then 0
        else abs(a / gcd(a)(b) * b)

let divMod a b = 
    (let q0 = a / b
    in 
        let r0 = a - q0 * b
        in 
            if r0 < 0
            then 
                if b > 0
                then (q0 - 1, r0 + b)
                else (q0 + 1, r0 - b)
            else (q0, r0))

let rec powGo acc base exp = 
    if exp == 0
    then acc
    else 
        let halfExp = exp / 2
        in 
            let squared = base * base
            in 
                if exp - halfExp * 2 == 1
                then powGo(acc * base)(squared)(halfExp)
                else powGo(acc)(squared)(halfExp)

let pow base exp = powGo(1)(base)(exp)

let rec isqrtGo x n = 
    (let y = (x + n / x) / 2
    in 
        if y < x
        then isqrtGo(y)(n)
        else x)

let isqrt n = 
    if n == 0
    then 0
    else isqrtGo(n)(n)
