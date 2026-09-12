package main

import ("fmt"; "math"; "os"; "strconv")

const pi = 3.141592653589793
const solarMass = 4 * pi * pi
const daysPerYear = 365.24

type Body struct{ x, y, z, vx, vy, vz, mass float64 }

var bodies = [5]Body{
	{0, 0, 0, 0, 0, 0, solarMass},
	{4.84143144246472090, -1.16032004402742839, -0.103622044471123109,
		0.00166007664274403694 * daysPerYear, 0.00769901118419740425 * daysPerYear,
		-0.0000690460016972063023 * daysPerYear, 0.000954791938424326609 * solarMass},
	{8.34336671824457987, 4.12479856412430479, -0.403523417114321381,
		-0.00276742510726862411 * daysPerYear, 0.00499852801234917238 * daysPerYear,
		0.0000230417297573763929 * daysPerYear, 0.000285885980666130812 * solarMass},
	{12.8943695621391310, -15.1111514016986312, -0.223307578892655734,
		0.00296460137564761618 * daysPerYear, 0.00237847173959480950 * daysPerYear,
		-0.0000296589568540237556 * daysPerYear, 0.0000436624404335156298 * solarMass},
	{15.3796971148509165, -25.9193146099879641, 0.179258772950371181,
		0.00268067772490389322 * daysPerYear, 0.00162824170038242295 * daysPerYear,
		-0.0000951592254519715870 * daysPerYear, 0.0000515138902046611451 * solarMass},
}

func offsetMomentum(b *[5]Body) {
	var px, py, pz float64
	for i := range b {
		px += b[i].vx * b[i].mass
		py += b[i].vy * b[i].mass
		pz += b[i].vz * b[i].mass
	}
	b[0].vx = -px / solarMass
	b[0].vy = -py / solarMass
	b[0].vz = -pz / solarMass
}

func advance(b *[5]Body, dt float64) {
	for i := 0; i < 5; i++ {
		for j := i + 1; j < 5; j++ {
			dx := b[i].x - b[j].x
			dy := b[i].y - b[j].y
			dz := b[i].z - b[j].z
			d2 := dx*dx + dy*dy + dz*dz
			mag := dt / (d2 * math.Sqrt(d2))
			mj := b[j].mass * mag
			mi := b[i].mass * mag
			b[i].vx -= dx * mj; b[i].vy -= dy * mj; b[i].vz -= dz * mj
			b[j].vx += dx * mi; b[j].vy += dy * mi; b[j].vz += dz * mi
		}
	}
	for i := range b {
		b[i].x += dt * b[i].vx
		b[i].y += dt * b[i].vy
		b[i].z += dt * b[i].vz
	}
}

func energy(b *[5]Body) float64 {
	var e float64
	for i := 0; i < 5; i++ {
		e += 0.5 * b[i].mass * (b[i].vx*b[i].vx + b[i].vy*b[i].vy + b[i].vz*b[i].vz)
		for j := i + 1; j < 5; j++ {
			dx := b[i].x - b[j].x
			dy := b[i].y - b[j].y
			dz := b[i].z - b[j].z
			e -= b[i].mass * b[j].mass / math.Sqrt(dx*dx+dy*dy+dz*dz)
		}
	}
	return e
}

func main() {
	n := 1000
	if len(os.Args) > 1 {
		if v, err := strconv.Atoi(os.Args[1]); err == nil { n = v }
	}
	b := bodies
	offsetMomentum(&b)
	fmt.Printf("%.9f\n", energy(&b))
	for i := 0; i < n; i++ { advance(&b, 0.01) }
	fmt.Printf("%.9f\n", energy(&b))
}
