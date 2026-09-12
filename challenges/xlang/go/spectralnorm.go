package main

import ("fmt"; "math"; "os"; "strconv")

func a(i, j int) float64 { return 1.0 / float64((i+j)*(i+j+1)/2+i+1) }

func mulAv(v, out []float64) {
	n := len(v)
	for i := 0; i < n; i++ { s := 0.0; for j := 0; j < n; j++ { s += a(i, j) * v[j] }; out[i] = s }
}
func mulAtv(v, out []float64) {
	n := len(v)
	for i := 0; i < n; i++ { s := 0.0; for j := 0; j < n; j++ { s += a(j, i) * v[j] }; out[i] = s }
}
func mulAtAv(v, out, tmp []float64) { mulAv(v, tmp); mulAtv(tmp, out) }

func main() {
	n := 100
	if len(os.Args) > 1 { if x, err := strconv.Atoi(os.Args[1]); err == nil { n = x } }
	u := make([]float64, n); v := make([]float64, n); tmp := make([]float64, n)
	for i := range u { u[i] = 1.0 }
	for i := 0; i < 10; i++ { mulAtAv(u, v, tmp); mulAtAv(v, u, tmp) }
	vbv, vv := 0.0, 0.0
	for i := 0; i < n; i++ { vbv += u[i] * v[i]; vv += v[i] * v[i] }
	fmt.Printf("%.9f\n", math.Sqrt(vbv/vv))
}
