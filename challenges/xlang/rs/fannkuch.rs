fn fannkuch(n: usize) -> (i32, i32) {
    let mut perm = vec![0usize; n];
    let mut perm1: Vec<usize> = (0..n).collect();
    let mut count = vec![0usize; n];
    let (mut max_flips, mut checksum, mut perm_count) = (0i32, 0i32, 0i64);
    let mut r = n;
    loop {
        while r != 1 { count[r - 1] = r; r -= 1; }
        perm.copy_from_slice(&perm1);
        let mut flips = 0i32;
        loop {
            let k = perm[0];
            if k == 0 { break; }
            let k2 = (k + 1) >> 1;
            for i in 0..k2 { perm.swap(i, k - i); }
            flips += 1;
        }
        if flips > max_flips { max_flips = flips; }
        checksum += if perm_count % 2 == 0 { flips } else { -flips };
        loop {
            if r == n { return (checksum, max_flips); }
            let perm0 = perm1[0];
            let mut i = 0;
            while i < r { perm1[i] = perm1[i + 1]; i += 1; }
            perm1[r] = perm0;
            count[r] -= 1;
            if count[r] > 0 { break; }
            r += 1;
        }
        perm_count += 1;
    }
}

fn main() {
    let n: usize = std::env::args().nth(1).and_then(|a| a.parse().ok()).unwrap_or(7);
    let (checksum, max_flips) = fannkuch(n);
    println!("{}\nPfannkuchen({}) = {}", checksum, n, max_flips);
}
