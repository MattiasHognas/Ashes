const PI: f64 = 3.141592653589793;
const SOLAR_MASS: f64 = 4.0 * PI * PI;
const DAYS_PER_YEAR: f64 = 365.24;

#[derive(Clone, Copy)]
struct Body { x: f64, y: f64, z: f64, vx: f64, vy: f64, vz: f64, mass: f64 }

fn bodies() -> [Body; 5] {
    [
        Body { x: 0.0, y: 0.0, z: 0.0, vx: 0.0, vy: 0.0, vz: 0.0, mass: SOLAR_MASS },
        Body { x: 4.84143144246472090, y: -1.16032004402742839, z: -0.103622044471123109,
               vx: 0.00166007664274403694 * DAYS_PER_YEAR, vy: 0.00769901118419740425 * DAYS_PER_YEAR,
               vz: -0.0000690460016972063023 * DAYS_PER_YEAR, mass: 0.000954791938424326609 * SOLAR_MASS },
        Body { x: 8.34336671824457987, y: 4.12479856412430479, z: -0.403523417114321381,
               vx: -0.00276742510726862411 * DAYS_PER_YEAR, vy: 0.00499852801234917238 * DAYS_PER_YEAR,
               vz: 0.0000230417297573763929 * DAYS_PER_YEAR, mass: 0.000285885980666130812 * SOLAR_MASS },
        Body { x: 12.8943695621391310, y: -15.1111514016986312, z: -0.223307578892655734,
               vx: 0.00296460137564761618 * DAYS_PER_YEAR, vy: 0.00237847173959480950 * DAYS_PER_YEAR,
               vz: -0.0000296589568540237556 * DAYS_PER_YEAR, mass: 0.0000436624404335156298 * SOLAR_MASS },
        Body { x: 15.3796971148509165, y: -25.9193146099879641, z: 0.179258772950371181,
               vx: 0.00268067772490389322 * DAYS_PER_YEAR, vy: 0.00162824170038242295 * DAYS_PER_YEAR,
               vz: -0.0000951592254519715870 * DAYS_PER_YEAR, mass: 0.0000515138902046611451 * SOLAR_MASS },
    ]
}

fn offset_momentum(b: &mut [Body; 5]) {
    let (mut px, mut py, mut pz) = (0.0, 0.0, 0.0);
    for x in b.iter() { px += x.vx * x.mass; py += x.vy * x.mass; pz += x.vz * x.mass; }
    b[0].vx = -px / SOLAR_MASS; b[0].vy = -py / SOLAR_MASS; b[0].vz = -pz / SOLAR_MASS;
}

fn advance(b: &mut [Body; 5], dt: f64) {
    for i in 0..5 {
        for j in (i + 1)..5 {
            let dx = b[i].x - b[j].x;
            let dy = b[i].y - b[j].y;
            let dz = b[i].z - b[j].z;
            let d2 = dx * dx + dy * dy + dz * dz;
            let mag = dt / (d2 * d2.sqrt());
            let mj = b[j].mass * mag;
            let mi = b[i].mass * mag;
            b[i].vx -= dx * mj; b[i].vy -= dy * mj; b[i].vz -= dz * mj;
            b[j].vx += dx * mi; b[j].vy += dy * mi; b[j].vz += dz * mi;
        }
    }
    for x in b.iter_mut() { x.x += dt * x.vx; x.y += dt * x.vy; x.z += dt * x.vz; }
}

fn energy(b: &[Body; 5]) -> f64 {
    let mut e = 0.0;
    for i in 0..5 {
        let bi = &b[i];
        e += 0.5 * bi.mass * (bi.vx * bi.vx + bi.vy * bi.vy + bi.vz * bi.vz);
        for j in (i + 1)..5 {
            let dx = bi.x - b[j].x;
            let dy = bi.y - b[j].y;
            let dz = bi.z - b[j].z;
            e -= bi.mass * b[j].mass / (dx * dx + dy * dy + dz * dz).sqrt();
        }
    }
    e
}

fn main() {
    let n: i64 = std::env::args().nth(1).and_then(|a| a.parse().ok()).unwrap_or(1000);
    let mut b = bodies();
    offset_momentum(&mut b);
    println!("{:.9}", energy(&b));
    for _ in 0..n { advance(&mut b, 0.01); }
    println!("{:.9}", energy(&b));
}
