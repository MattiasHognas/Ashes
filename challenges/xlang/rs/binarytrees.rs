enum Tree { Leaf, Node(Box<Tree>, Box<Tree>) }

fn make(depth: i32) -> Tree {
    if depth == 0 { Tree::Leaf } else { Tree::Node(Box::new(make(depth - 1)), Box::new(make(depth - 1))) }
}
fn check(t: &Tree) -> i64 {
    match t { Tree::Leaf => 1, Tree::Node(l, r) => 1 + check(l) + check(r) }
}

fn main() {
    let n: i32 = std::env::args().nth(1).and_then(|a| a.parse().ok()).unwrap_or(10);
    let min_depth = 4;
    let max_depth = if min_depth + 2 > n { min_depth + 2 } else { n };
    let stretch_depth = max_depth + 1;
    let long_lived = make(max_depth);
    let mut out = String::new();
    out.push_str(&format!("stretch tree of depth {}\t check: {}\n", stretch_depth, check(&make(stretch_depth))));
    let mut depth = min_depth;
    while depth <= max_depth {
        let iterations = 1i64 << (max_depth - depth + min_depth);
        let mut sum = 0i64;
        for _ in 0..iterations { sum += check(&make(depth)); }
        out.push_str(&format!("{}\t trees of depth {}\t check: {}\n", iterations, depth, sum));
        depth += 2;
    }
    out.push_str(&format!("long lived tree of depth {}\t check: {}\n", max_depth, check(&long_lived)));
    print!("{}", out);
}
