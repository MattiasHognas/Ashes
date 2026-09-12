package main

import ("fmt"; "os"; "strconv")

func fannkuch(n int) (int, int) {
	perm := make([]int, n)
	perm1 := make([]int, n)
	count := make([]int, n)
	for i := range perm1 { perm1[i] = i }
	maxFlips, checksum, permCount := 0, 0, 0
	r := n
	for {
		for r != 1 { count[r-1] = r; r-- }
		copy(perm, perm1)
		flips := 0
		for {
			k := perm[0]
			if k == 0 { break }
			k2 := (k + 1) >> 1
			for i := 0; i < k2; i++ { perm[i], perm[k-i] = perm[k-i], perm[i] }
			flips++
		}
		if flips > maxFlips { maxFlips = flips }
		if permCount%2 == 0 { checksum += flips } else { checksum -= flips }
		for {
			if r == n { return checksum, maxFlips }
			perm0 := perm1[0]
			i := 0
			for i < r { perm1[i] = perm1[i+1]; i++ }
			perm1[r] = perm0
			count[r]--
			if count[r] > 0 { break }
			r++
		}
		permCount++
	}
}

func main() {
	n := 7
	if len(os.Args) > 1 { if x, err := strconv.Atoi(os.Args[1]); err == nil { n = x } }
	checksum, maxFlips := fannkuch(n)
	fmt.Printf("%d\nPfannkuchen(%d) = %d\n", checksum, n, maxFlips)
}
