// Generated from bundled PCRE2 10.48 by scripts/selfhost-dev/generate-lexer-unicode.sh.
// Preserves the lexer's fixed Unicode letter, letter-or-decimal-digit, and whitespace checks.
// Balanced integer decisions allocate no runtime tables. Regenerate when Pcre2Version changes.
export (
    value isUnicodeLetter,
    value isUnicodeIdentifierCharacter,
    value isUnicodeWhiteSpace,
)

let isUnicodeLetter (code: Int) =
    if code < 43777
    then
        if code < 4301
        then
            if code < 2741
            then
                if code < 2036
                then
                    if code < 931
                    then
                        if code < 736
                        then
                            if code < 186
                            then
                                if code < 97
                                then
                                    if code < 65
                                    then false
                                    else
                                        if code <= 90
                                        then true
                                        else false
                                else
                                    if code <= 122
                                    then true
                                    else
                                        if code < 170
                                        then false
                                        else
                                            if code <= 170
                                            then true
                                            else
                                                if code < 181
                                                then false
                                                else
                                                    if code <= 181
                                                    then true
                                                    else false
                            else
                                if code <= 186
                                then true
                                else
                                    if code < 216
                                    then
                                        if code < 192
                                        then false
                                        else
                                            if code <= 214
                                            then true
                                            else false
                                    else
                                        if code <= 246
                                        then true
                                        else
                                            if code < 248
                                            then false
                                            else
                                                if code <= 705
                                                then true
                                                else
                                                    if code < 710
                                                    then false
                                                    else
                                                        if code <= 721
                                                        then true
                                                        else false
                        else
                            if code <= 740
                            then true
                            else
                                if code < 890
                                then
                                    if code < 750
                                    then
                                        if code < 748
                                        then false
                                        else
                                            if code <= 748
                                            then true
                                            else false
                                    else
                                        if code <= 750
                                        then true
                                        else
                                            if code < 880
                                            then false
                                            else
                                                if code <= 884
                                                then true
                                                else
                                                    if code < 886
                                                    then false
                                                    else
                                                        if code <= 887
                                                        then true
                                                        else false
                                else
                                    if code <= 893
                                    then true
                                    else
                                        if code < 904
                                        then
                                            if code < 895
                                            then false
                                            else
                                                if code <= 895
                                                then true
                                                else
                                                    if code < 902
                                                    then false
                                                    else
                                                        if code <= 902
                                                        then true
                                                        else false
                                        else
                                            if code <= 906
                                            then true
                                            else
                                                if code < 908
                                                then false
                                                else
                                                    if code <= 908
                                                    then true
                                                    else
                                                        if code < 910
                                                        then false
                                                        else
                                                            if code <= 929
                                                            then true
                                                            else false
                    else
                        if code <= 1013
                        then true
                        else
                            if code < 1649
                            then
                                if code < 1376
                                then
                                    if code < 1162
                                    then
                                        if code < 1015
                                        then false
                                        else
                                            if code <= 1153
                                            then true
                                            else false
                                    else
                                        if code <= 1327
                                        then true
                                        else
                                            if code < 1329
                                            then false
                                            else
                                                if code <= 1366
                                                then true
                                                else
                                                    if code < 1369
                                                    then false
                                                    else
                                                        if code <= 1369
                                                        then true
                                                        else false
                                else
                                    if code <= 1416
                                    then true
                                    else
                                        if code < 1519
                                        then
                                            if code < 1488
                                            then false
                                            else
                                                if code <= 1514
                                                then true
                                                else false
                                        else
                                            if code <= 1522
                                            then true
                                            else
                                                if code < 1568
                                                then false
                                                else
                                                    if code <= 1610
                                                    then true
                                                    else
                                                        if code < 1646
                                                        then false
                                                        else
                                                            if code <= 1647
                                                            then true
                                                            else false
                            else
                                if code <= 1747
                                then true
                                else
                                    if code < 1791
                                    then
                                        if code < 1765
                                        then
                                            if code < 1749
                                            then false
                                            else
                                                if code <= 1749
                                                then true
                                                else false
                                        else
                                            if code <= 1766
                                            then true
                                            else
                                                if code < 1774
                                                then false
                                                else
                                                    if code <= 1775
                                                    then true
                                                    else
                                                        if code < 1786
                                                        then false
                                                        else
                                                            if code <= 1788
                                                            then true
                                                            else false
                                    else
                                        if code <= 1791
                                        then true
                                        else
                                            if code < 1869
                                            then
                                                if code < 1808
                                                then false
                                                else
                                                    if code <= 1808
                                                    then true
                                                    else
                                                        if code < 1810
                                                        then false
                                                        else
                                                            if code <= 1839
                                                            then true
                                                            else false
                                            else
                                                if code <= 1957
                                                then true
                                                else
                                                    if code < 1969
                                                    then false
                                                    else
                                                        if code <= 1969
                                                        then true
                                                        else
                                                            if code < 1994
                                                            then false
                                                            else
                                                                if code <= 2026
                                                                then true
                                                                else false
                else
                    if code <= 2037
                    then true
                    else
                        if code < 2486
                        then
                            if code < 2208
                            then
                                if code < 2088
                                then
                                    if code < 2048
                                    then
                                        if code < 2042
                                        then false
                                        else
                                            if code <= 2042
                                            then true
                                            else false
                                    else
                                        if code <= 2069
                                        then true
                                        else
                                            if code < 2074
                                            then false
                                            else
                                                if code <= 2074
                                                then true
                                                else
                                                    if code < 2084
                                                    then false
                                                    else
                                                        if code <= 2084
                                                        then true
                                                        else false
                                else
                                    if code <= 2088
                                    then true
                                    else
                                        if code < 2144
                                        then
                                            if code < 2112
                                            then false
                                            else
                                                if code <= 2136
                                                then true
                                                else false
                                        else
                                            if code <= 2154
                                            then true
                                            else
                                                if code < 2160
                                                then false
                                                else
                                                    if code <= 2183
                                                    then true
                                                    else
                                                        if code < 2185
                                                        then false
                                                        else
                                                            if code <= 2191
                                                            then true
                                                            else false
                            else
                                if code <= 2249
                                then true
                                else
                                    if code < 2417
                                    then
                                        if code < 2365
                                        then
                                            if code < 2308
                                            then false
                                            else
                                                if code <= 2361
                                                then true
                                                else false
                                        else
                                            if code <= 2365
                                            then true
                                            else
                                                if code < 2384
                                                then false
                                                else
                                                    if code <= 2384
                                                    then true
                                                    else
                                                        if code < 2392
                                                        then false
                                                        else
                                                            if code <= 2401
                                                            then true
                                                            else false
                                    else
                                        if code <= 2432
                                        then true
                                        else
                                            if code < 2451
                                            then
                                                if code < 2437
                                                then false
                                                else
                                                    if code <= 2444
                                                    then true
                                                    else
                                                        if code < 2447
                                                        then false
                                                        else
                                                            if code <= 2448
                                                            then true
                                                            else false
                                            else
                                                if code <= 2472
                                                then true
                                                else
                                                    if code < 2474
                                                    then false
                                                    else
                                                        if code <= 2480
                                                        then true
                                                        else
                                                            if code < 2482
                                                            then false
                                                            else
                                                                if code <= 2482
                                                                then true
                                                                else false
                        else
                            if code <= 2489
                            then true
                            else
                                if code < 2610
                                then
                                    if code < 2544
                                    then
                                        if code < 2510
                                        then
                                            if code < 2493
                                            then false
                                            else
                                                if code <= 2493
                                                then true
                                                else false
                                        else
                                            if code <= 2510
                                            then true
                                            else
                                                if code < 2524
                                                then false
                                                else
                                                    if code <= 2525
                                                    then true
                                                    else
                                                        if code < 2527
                                                        then false
                                                        else
                                                            if code <= 2529
                                                            then true
                                                            else false
                                    else
                                        if code <= 2545
                                        then true
                                        else
                                            if code < 2575
                                            then
                                                if code < 2556
                                                then false
                                                else
                                                    if code <= 2556
                                                    then true
                                                    else
                                                        if code < 2565
                                                        then false
                                                        else
                                                            if code <= 2570
                                                            then true
                                                            else false
                                            else
                                                if code <= 2576
                                                then true
                                                else
                                                    if code < 2579
                                                    then false
                                                    else
                                                        if code <= 2600
                                                        then true
                                                        else
                                                            if code < 2602
                                                            then false
                                                            else
                                                                if code <= 2608
                                                                then true
                                                                else false
                                else
                                    if code <= 2611
                                    then true
                                    else
                                        if code < 2674
                                        then
                                            if code < 2616
                                            then
                                                if code < 2613
                                                then false
                                                else
                                                    if code <= 2614
                                                    then true
                                                    else false
                                            else
                                                if code <= 2617
                                                then true
                                                else
                                                    if code < 2649
                                                    then false
                                                    else
                                                        if code <= 2652
                                                        then true
                                                        else
                                                            if code < 2654
                                                            then false
                                                            else
                                                                if code <= 2654
                                                                then true
                                                                else false
                                        else
                                            if code <= 2676
                                            then true
                                            else
                                                if code < 2707
                                                then
                                                    if code < 2693
                                                    then false
                                                    else
                                                        if code <= 2701
                                                        then true
                                                        else
                                                            if code < 2703
                                                            then false
                                                            else
                                                                if code <= 2705
                                                                then true
                                                                else false
                                                else
                                                    if code <= 2728
                                                    then true
                                                    else
                                                        if code < 2730
                                                        then false
                                                        else
                                                            if code <= 2736
                                                            then true
                                                            else
                                                                if code < 2738
                                                                then false
                                                                else
                                                                    if code <= 2739
                                                                    then true
                                                                    else false
            else
                if code <= 2745
                then true
                else
                    if code < 3313
                    then
                        if code < 2974
                        then
                            if code < 2869
                            then
                                if code < 2821
                                then
                                    if code < 2768
                                    then
                                        if code < 2749
                                        then false
                                        else
                                            if code <= 2749
                                            then true
                                            else false
                                    else
                                        if code <= 2768
                                        then true
                                        else
                                            if code < 2784
                                            then false
                                            else
                                                if code <= 2785
                                                then true
                                                else
                                                    if code < 2809
                                                    then false
                                                    else
                                                        if code <= 2809
                                                        then true
                                                        else false
                                else
                                    if code <= 2828
                                    then true
                                    else
                                        if code < 2835
                                        then
                                            if code < 2831
                                            then false
                                            else
                                                if code <= 2832
                                                then true
                                                else false
                                        else
                                            if code <= 2856
                                            then true
                                            else
                                                if code < 2858
                                                then false
                                                else
                                                    if code <= 2864
                                                    then true
                                                    else
                                                        if code < 2866
                                                        then false
                                                        else
                                                            if code <= 2867
                                                            then true
                                                            else false
                            else
                                if code <= 2873
                                then true
                                else
                                    if code < 2947
                                    then
                                        if code < 2908
                                        then
                                            if code < 2877
                                            then false
                                            else
                                                if code <= 2877
                                                then true
                                                else false
                                        else
                                            if code <= 2909
                                            then true
                                            else
                                                if code < 2911
                                                then false
                                                else
                                                    if code <= 2913
                                                    then true
                                                    else
                                                        if code < 2929
                                                        then false
                                                        else
                                                            if code <= 2929
                                                            then true
                                                            else false
                                    else
                                        if code <= 2947
                                        then true
                                        else
                                            if code < 2962
                                            then
                                                if code < 2949
                                                then false
                                                else
                                                    if code <= 2954
                                                    then true
                                                    else
                                                        if code < 2958
                                                        then false
                                                        else
                                                            if code <= 2960
                                                            then true
                                                            else false
                                            else
                                                if code <= 2965
                                                then true
                                                else
                                                    if code < 2969
                                                    then false
                                                    else
                                                        if code <= 2970
                                                        then true
                                                        else
                                                            if code < 2972
                                                            then false
                                                            else
                                                                if code <= 2972
                                                                then true
                                                                else false
                        else
                            if code <= 2975
                            then true
                            else
                                if code < 3164
                                then
                                    if code < 3077
                                    then
                                        if code < 2984
                                        then
                                            if code < 2979
                                            then false
                                            else
                                                if code <= 2980
                                                then true
                                                else false
                                        else
                                            if code <= 2986
                                            then true
                                            else
                                                if code < 2990
                                                then false
                                                else
                                                    if code <= 3001
                                                    then true
                                                    else
                                                        if code < 3024
                                                        then false
                                                        else
                                                            if code <= 3024
                                                            then true
                                                            else false
                                    else
                                        if code <= 3084
                                        then true
                                        else
                                            if code < 3114
                                            then
                                                if code < 3086
                                                then false
                                                else
                                                    if code <= 3088
                                                    then true
                                                    else
                                                        if code < 3090
                                                        then false
                                                        else
                                                            if code <= 3112
                                                            then true
                                                            else false
                                            else
                                                if code <= 3129
                                                then true
                                                else
                                                    if code < 3133
                                                    then false
                                                    else
                                                        if code <= 3133
                                                        then true
                                                        else
                                                            if code < 3160
                                                            then false
                                                            else
                                                                if code <= 3162
                                                                then true
                                                                else false
                                else
                                    if code <= 3165
                                    then true
                                    else
                                        if code < 3218
                                        then
                                            if code < 3200
                                            then
                                                if code < 3168
                                                then false
                                                else
                                                    if code <= 3169
                                                    then true
                                                    else false
                                            else
                                                if code <= 3200
                                                then true
                                                else
                                                    if code < 3205
                                                    then false
                                                    else
                                                        if code <= 3212
                                                        then true
                                                        else
                                                            if code < 3214
                                                            then false
                                                            else
                                                                if code <= 3216
                                                                then true
                                                                else false
                                        else
                                            if code <= 3240
                                            then true
                                            else
                                                if code < 3261
                                                then
                                                    if code < 3242
                                                    then false
                                                    else
                                                        if code <= 3251
                                                        then true
                                                        else
                                                            if code < 3253
                                                            then false
                                                            else
                                                                if code <= 3257
                                                                then true
                                                                else false
                                                else
                                                    if code <= 3261
                                                    then true
                                                    else
                                                        if code < 3292
                                                        then false
                                                        else
                                                            if code <= 3294
                                                            then true
                                                            else
                                                                if code < 3296
                                                                then false
                                                                else
                                                                    if code <= 3297
                                                                    then true
                                                                    else false
                    else
                        if code <= 3314
                        then true
                        else
                            if code < 3749
                            then
                                if code < 3482
                                then
                                    if code < 3406
                                    then
                                        if code < 3342
                                        then
                                            if code < 3332
                                            then false
                                            else
                                                if code <= 3340
                                                then true
                                                else false
                                        else
                                            if code <= 3344
                                            then true
                                            else
                                                if code < 3346
                                                then false
                                                else
                                                    if code <= 3386
                                                    then true
                                                    else
                                                        if code < 3389
                                                        then false
                                                        else
                                                            if code <= 3389
                                                            then true
                                                            else false
                                    else
                                        if code <= 3406
                                        then true
                                        else
                                            if code < 3423
                                            then
                                                if code < 3412
                                                then false
                                                else
                                                    if code <= 3414
                                                    then true
                                                    else false
                                            else
                                                if code <= 3425
                                                then true
                                                else
                                                    if code < 3450
                                                    then false
                                                    else
                                                        if code <= 3455
                                                        then true
                                                        else
                                                            if code < 3461
                                                            then false
                                                            else
                                                                if code <= 3478
                                                                then true
                                                                else false
                                else
                                    if code <= 3505
                                    then true
                                    else
                                        if code < 3634
                                        then
                                            if code < 3517
                                            then
                                                if code < 3507
                                                then false
                                                else
                                                    if code <= 3515
                                                    then true
                                                    else false
                                            else
                                                if code <= 3517
                                                then true
                                                else
                                                    if code < 3520
                                                    then false
                                                    else
                                                        if code <= 3526
                                                        then true
                                                        else
                                                            if code < 3585
                                                            then false
                                                            else
                                                                if code <= 3632
                                                                then true
                                                                else false
                                        else
                                            if code <= 3635
                                            then true
                                            else
                                                if code < 3716
                                                then
                                                    if code < 3648
                                                    then false
                                                    else
                                                        if code <= 3654
                                                        then true
                                                        else
                                                            if code < 3713
                                                            then false
                                                            else
                                                                if code <= 3714
                                                                then true
                                                                else false
                                                else
                                                    if code <= 3716
                                                    then true
                                                    else
                                                        if code < 3718
                                                        then false
                                                        else
                                                            if code <= 3722
                                                            then true
                                                            else
                                                                if code < 3724
                                                                then false
                                                                else
                                                                    if code <= 3747
                                                                    then true
                                                                    else false
                            else
                                if code <= 3749
                                then true
                                else
                                    if code < 4096
                                    then
                                        if code < 3782
                                        then
                                            if code < 3762
                                            then
                                                if code < 3751
                                                then false
                                                else
                                                    if code <= 3760
                                                    then true
                                                    else false
                                            else
                                                if code <= 3763
                                                then true
                                                else
                                                    if code < 3773
                                                    then false
                                                    else
                                                        if code <= 3773
                                                        then true
                                                        else
                                                            if code < 3776
                                                            then false
                                                            else
                                                                if code <= 3780
                                                                then true
                                                                else false
                                        else
                                            if code <= 3782
                                            then true
                                            else
                                                if code < 3904
                                                then
                                                    if code < 3804
                                                    then false
                                                    else
                                                        if code <= 3807
                                                        then true
                                                        else
                                                            if code < 3840
                                                            then false
                                                            else
                                                                if code <= 3840
                                                                then true
                                                                else false
                                                else
                                                    if code <= 3911
                                                    then true
                                                    else
                                                        if code < 3913
                                                        then false
                                                        else
                                                            if code <= 3948
                                                            then true
                                                            else
                                                                if code < 3976
                                                                then false
                                                                else
                                                                    if code <= 3980
                                                                    then true
                                                                    else false
                                    else
                                        if code <= 4138
                                        then true
                                        else
                                            if code < 4197
                                            then
                                                if code < 4176
                                                then
                                                    if code < 4159
                                                    then false
                                                    else
                                                        if code <= 4159
                                                        then true
                                                        else false
                                                else
                                                    if code <= 4181
                                                    then true
                                                    else
                                                        if code < 4186
                                                        then false
                                                        else
                                                            if code <= 4189
                                                            then true
                                                            else
                                                                if code < 4193
                                                                then false
                                                                else
                                                                    if code <= 4193
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 4198
                                                then true
                                                else
                                                    if code < 4238
                                                    then
                                                        if code < 4206
                                                        then false
                                                        else
                                                            if code <= 4208
                                                            then true
                                                            else
                                                                if code < 4213
                                                                then false
                                                                else
                                                                    if code <= 4225
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 4238
                                                        then true
                                                        else
                                                            if code < 4256
                                                            then false
                                                            else
                                                                if code <= 4293
                                                                then true
                                                                else
                                                                    if code < 4295
                                                                    then false
                                                                    else
                                                                        if code <= 4295
                                                                        then true
                                                                        else false
        else
            if code <= 4301
            then true
            else
                if code < 8450
                then
                    if code < 6528
                    then
                        if code < 5121
                        then
                            if code < 4786
                            then
                                if code < 4696
                                then
                                    if code < 4348
                                    then
                                        if code < 4304
                                        then false
                                        else
                                            if code <= 4346
                                            then true
                                            else false
                                    else
                                        if code <= 4680
                                        then true
                                        else
                                            if code < 4682
                                            then false
                                            else
                                                if code <= 4685
                                                then true
                                                else
                                                    if code < 4688
                                                    then false
                                                    else
                                                        if code <= 4694
                                                        then true
                                                        else false
                                else
                                    if code <= 4696
                                    then true
                                    else
                                        if code < 4704
                                        then
                                            if code < 4698
                                            then false
                                            else
                                                if code <= 4701
                                                then true
                                                else false
                                        else
                                            if code <= 4744
                                            then true
                                            else
                                                if code < 4746
                                                then false
                                                else
                                                    if code <= 4749
                                                    then true
                                                    else
                                                        if code < 4752
                                                        then false
                                                        else
                                                            if code <= 4784
                                                            then true
                                                            else false
                            else
                                if code <= 4789
                                then true
                                else
                                    if code < 4824
                                    then
                                        if code < 4800
                                        then
                                            if code < 4792
                                            then false
                                            else
                                                if code <= 4798
                                                then true
                                                else false
                                        else
                                            if code <= 4800
                                            then true
                                            else
                                                if code < 4802
                                                then false
                                                else
                                                    if code <= 4805
                                                    then true
                                                    else
                                                        if code < 4808
                                                        then false
                                                        else
                                                            if code <= 4822
                                                            then true
                                                            else false
                                    else
                                        if code <= 4880
                                        then true
                                        else
                                            if code < 4992
                                            then
                                                if code < 4882
                                                then false
                                                else
                                                    if code <= 4885
                                                    then true
                                                    else
                                                        if code < 4888
                                                        then false
                                                        else
                                                            if code <= 4954
                                                            then true
                                                            else false
                                            else
                                                if code <= 5007
                                                then true
                                                else
                                                    if code < 5024
                                                    then false
                                                    else
                                                        if code <= 5109
                                                        then true
                                                        else
                                                            if code < 5112
                                                            then false
                                                            else
                                                                if code <= 5117
                                                                then true
                                                                else false
                        else
                            if code <= 5740
                            then true
                            else
                                if code < 6016
                                then
                                    if code < 5888
                                    then
                                        if code < 5761
                                        then
                                            if code < 5743
                                            then false
                                            else
                                                if code <= 5759
                                                then true
                                                else false
                                        else
                                            if code <= 5786
                                            then true
                                            else
                                                if code < 5792
                                                then false
                                                else
                                                    if code <= 5866
                                                    then true
                                                    else
                                                        if code < 5873
                                                        then false
                                                        else
                                                            if code <= 5880
                                                            then true
                                                            else false
                                    else
                                        if code <= 5905
                                        then true
                                        else
                                            if code < 5952
                                            then
                                                if code < 5919
                                                then false
                                                else
                                                    if code <= 5937
                                                    then true
                                                    else false
                                            else
                                                if code <= 5969
                                                then true
                                                else
                                                    if code < 5984
                                                    then false
                                                    else
                                                        if code <= 5996
                                                        then true
                                                        else
                                                            if code < 5998
                                                            then false
                                                            else
                                                                if code <= 6000
                                                                then true
                                                                else false
                                else
                                    if code <= 6067
                                    then true
                                    else
                                        if code < 6279
                                        then
                                            if code < 6108
                                            then
                                                if code < 6103
                                                then false
                                                else
                                                    if code <= 6103
                                                    then true
                                                    else false
                                            else
                                                if code <= 6108
                                                then true
                                                else
                                                    if code < 6176
                                                    then false
                                                    else
                                                        if code <= 6264
                                                        then true
                                                        else
                                                            if code < 6272
                                                            then false
                                                            else
                                                                if code <= 6276
                                                                then true
                                                                else false
                                        else
                                            if code <= 6312
                                            then true
                                            else
                                                if code < 6400
                                                then
                                                    if code < 6314
                                                    then false
                                                    else
                                                        if code <= 6314
                                                        then true
                                                        else
                                                            if code < 6320
                                                            then false
                                                            else
                                                                if code <= 6389
                                                                then true
                                                                else false
                                                else
                                                    if code <= 6430
                                                    then true
                                                    else
                                                        if code < 6480
                                                        then false
                                                        else
                                                            if code <= 6509
                                                            then true
                                                            else
                                                                if code < 6512
                                                                then false
                                                                else
                                                                    if code <= 6516
                                                                    then true
                                                                    else false
                    else
                        if code <= 6571
                        then true
                        else
                            if code < 7680
                            then
                                if code < 7168
                                then
                                    if code < 6917
                                    then
                                        if code < 6656
                                        then
                                            if code < 6576
                                            then false
                                            else
                                                if code <= 6601
                                                then true
                                                else false
                                        else
                                            if code <= 6678
                                            then true
                                            else
                                                if code < 6688
                                                then false
                                                else
                                                    if code <= 6740
                                                    then true
                                                    else
                                                        if code < 6823
                                                        then false
                                                        else
                                                            if code <= 6823
                                                            then true
                                                            else false
                                    else
                                        if code <= 6963
                                        then true
                                        else
                                            if code < 7043
                                            then
                                                if code < 6981
                                                then false
                                                else
                                                    if code <= 6988
                                                    then true
                                                    else false
                                            else
                                                if code <= 7072
                                                then true
                                                else
                                                    if code < 7086
                                                    then false
                                                    else
                                                        if code <= 7087
                                                        then true
                                                        else
                                                            if code < 7098
                                                            then false
                                                            else
                                                                if code <= 7141
                                                                then true
                                                                else false
                                else
                                    if code <= 7203
                                    then true
                                    else
                                        if code < 7357
                                        then
                                            if code < 7258
                                            then
                                                if code < 7245
                                                then false
                                                else
                                                    if code <= 7247
                                                    then true
                                                    else false
                                            else
                                                if code <= 7293
                                                then true
                                                else
                                                    if code < 7296
                                                    then false
                                                    else
                                                        if code <= 7306
                                                        then true
                                                        else
                                                            if code < 7312
                                                            then false
                                                            else
                                                                if code <= 7354
                                                                then true
                                                                else false
                                        else
                                            if code <= 7359
                                            then true
                                            else
                                                if code < 7413
                                                then
                                                    if code < 7401
                                                    then false
                                                    else
                                                        if code <= 7404
                                                        then true
                                                        else
                                                            if code < 7406
                                                            then false
                                                            else
                                                                if code <= 7411
                                                                then true
                                                                else false
                                                else
                                                    if code <= 7414
                                                    then true
                                                    else
                                                        if code < 7418
                                                        then false
                                                        else
                                                            if code <= 7418
                                                            then true
                                                            else
                                                                if code < 7424
                                                                then false
                                                                else
                                                                    if code <= 7615
                                                                    then true
                                                                    else false
                            else
                                if code <= 7957
                                then true
                                else
                                    if code < 8126
                                    then
                                        if code < 8025
                                        then
                                            if code < 7968
                                            then
                                                if code < 7960
                                                then false
                                                else
                                                    if code <= 7965
                                                    then true
                                                    else false
                                            else
                                                if code <= 8005
                                                then true
                                                else
                                                    if code < 8008
                                                    then false
                                                    else
                                                        if code <= 8013
                                                        then true
                                                        else
                                                            if code < 8016
                                                            then false
                                                            else
                                                                if code <= 8023
                                                                then true
                                                                else false
                                        else
                                            if code <= 8025
                                            then true
                                            else
                                                if code < 8031
                                                then
                                                    if code < 8027
                                                    then false
                                                    else
                                                        if code <= 8027
                                                        then true
                                                        else
                                                            if code < 8029
                                                            then false
                                                            else
                                                                if code <= 8029
                                                                then true
                                                                else false
                                                else
                                                    if code <= 8061
                                                    then true
                                                    else
                                                        if code < 8064
                                                        then false
                                                        else
                                                            if code <= 8116
                                                            then true
                                                            else
                                                                if code < 8118
                                                                then false
                                                                else
                                                                    if code <= 8124
                                                                    then true
                                                                    else false
                                    else
                                        if code <= 8126
                                        then true
                                        else
                                            if code < 8160
                                            then
                                                if code < 8134
                                                then
                                                    if code < 8130
                                                    then false
                                                    else
                                                        if code <= 8132
                                                        then true
                                                        else false
                                                else
                                                    if code <= 8140
                                                    then true
                                                    else
                                                        if code < 8144
                                                        then false
                                                        else
                                                            if code <= 8147
                                                            then true
                                                            else
                                                                if code < 8150
                                                                then false
                                                                else
                                                                    if code <= 8155
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 8172
                                                then true
                                                else
                                                    if code < 8305
                                                    then
                                                        if code < 8178
                                                        then false
                                                        else
                                                            if code <= 8180
                                                            then true
                                                            else
                                                                if code < 8182
                                                                then false
                                                                else
                                                                    if code <= 8188
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 8305
                                                        then true
                                                        else
                                                            if code < 8319
                                                            then false
                                                            else
                                                                if code <= 8319
                                                                then true
                                                                else
                                                                    if code < 8336
                                                                    then false
                                                                    else
                                                                        if code <= 8348
                                                                        then true
                                                                        else false
                else
                    if code <= 8450
                    then true
                    else
                        if code < 13312
                        then
                            if code < 11631
                            then
                                if code < 8508
                                then
                                    if code < 8484
                                    then
                                        if code < 8458
                                        then
                                            if code < 8455
                                            then false
                                            else
                                                if code <= 8455
                                                then true
                                                else false
                                        else
                                            if code <= 8467
                                            then true
                                            else
                                                if code < 8469
                                                then false
                                                else
                                                    if code <= 8469
                                                    then true
                                                    else
                                                        if code < 8473
                                                        then false
                                                        else
                                                            if code <= 8477
                                                            then true
                                                            else false
                                    else
                                        if code <= 8484
                                        then true
                                        else
                                            if code < 8488
                                            then
                                                if code < 8486
                                                then false
                                                else
                                                    if code <= 8486
                                                    then true
                                                    else false
                                            else
                                                if code <= 8488
                                                then true
                                                else
                                                    if code < 8490
                                                    then false
                                                    else
                                                        if code <= 8493
                                                        then true
                                                        else
                                                            if code < 8495
                                                            then false
                                                            else
                                                                if code <= 8505
                                                                then true
                                                                else false
                                else
                                    if code <= 8511
                                    then true
                                    else
                                        if code < 11499
                                        then
                                            if code < 8526
                                            then
                                                if code < 8517
                                                then false
                                                else
                                                    if code <= 8521
                                                    then true
                                                    else false
                                            else
                                                if code <= 8526
                                                then true
                                                else
                                                    if code < 8579
                                                    then false
                                                    else
                                                        if code <= 8580
                                                        then true
                                                        else
                                                            if code < 11264
                                                            then false
                                                            else
                                                                if code <= 11492
                                                                then true
                                                                else false
                                        else
                                            if code <= 11502
                                            then true
                                            else
                                                if code < 11559
                                                then
                                                    if code < 11506
                                                    then false
                                                    else
                                                        if code <= 11507
                                                        then true
                                                        else
                                                            if code < 11520
                                                            then false
                                                            else
                                                                if code <= 11557
                                                                then true
                                                                else false
                                                else
                                                    if code <= 11559
                                                    then true
                                                    else
                                                        if code < 11565
                                                        then false
                                                        else
                                                            if code <= 11565
                                                            then true
                                                            else
                                                                if code < 11568
                                                                then false
                                                                else
                                                                    if code <= 11623
                                                                    then true
                                                                    else false
                            else
                                if code <= 11631
                                then true
                                else
                                    if code < 12293
                                    then
                                        if code < 11704
                                        then
                                            if code < 11680
                                            then
                                                if code < 11648
                                                then false
                                                else
                                                    if code <= 11670
                                                    then true
                                                    else false
                                            else
                                                if code <= 11686
                                                then true
                                                else
                                                    if code < 11688
                                                    then false
                                                    else
                                                        if code <= 11694
                                                        then true
                                                        else
                                                            if code < 11696
                                                            then false
                                                            else
                                                                if code <= 11702
                                                                then true
                                                                else false
                                        else
                                            if code <= 11710
                                            then true
                                            else
                                                if code < 11728
                                                then
                                                    if code < 11712
                                                    then false
                                                    else
                                                        if code <= 11718
                                                        then true
                                                        else
                                                            if code < 11720
                                                            then false
                                                            else
                                                                if code <= 11726
                                                                then true
                                                                else false
                                                else
                                                    if code <= 11734
                                                    then true
                                                    else
                                                        if code < 11736
                                                        then false
                                                        else
                                                            if code <= 11742
                                                            then true
                                                            else
                                                                if code < 11823
                                                                then false
                                                                else
                                                                    if code <= 11823
                                                                    then true
                                                                    else false
                                    else
                                        if code <= 12294
                                        then true
                                        else
                                            if code < 12449
                                            then
                                                if code < 12347
                                                then
                                                    if code < 12337
                                                    then false
                                                    else
                                                        if code <= 12341
                                                        then true
                                                        else false
                                                else
                                                    if code <= 12348
                                                    then true
                                                    else
                                                        if code < 12353
                                                        then false
                                                        else
                                                            if code <= 12438
                                                            then true
                                                            else
                                                                if code < 12445
                                                                then false
                                                                else
                                                                    if code <= 12447
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 12538
                                                then true
                                                else
                                                    if code < 12593
                                                    then
                                                        if code < 12540
                                                        then false
                                                        else
                                                            if code <= 12543
                                                            then true
                                                            else
                                                                if code < 12549
                                                                then false
                                                                else
                                                                    if code <= 12591
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 12686
                                                        then true
                                                        else
                                                            if code < 12704
                                                            then false
                                                            else
                                                                if code <= 12735
                                                                then true
                                                                else
                                                                    if code < 12784
                                                                    then false
                                                                    else
                                                                        if code <= 12799
                                                                        then true
                                                                        else false
                        else
                            if code <= 19903
                            then true
                            else
                                if code < 43274
                                then
                                    if code < 42786
                                    then
                                        if code < 42538
                                        then
                                            if code < 42192
                                            then
                                                if code < 19968
                                                then false
                                                else
                                                    if code <= 42124
                                                    then true
                                                    else false
                                            else
                                                if code <= 42237
                                                then true
                                                else
                                                    if code < 42240
                                                    then false
                                                    else
                                                        if code <= 42508
                                                        then true
                                                        else
                                                            if code < 42512
                                                            then false
                                                            else
                                                                if code <= 42527
                                                                then true
                                                                else false
                                        else
                                            if code <= 42539
                                            then true
                                            else
                                                if code < 42623
                                                then
                                                    if code < 42560
                                                    then false
                                                    else
                                                        if code <= 42606
                                                        then true
                                                        else false
                                                else
                                                    if code <= 42653
                                                    then true
                                                    else
                                                        if code < 42656
                                                        then false
                                                        else
                                                            if code <= 42725
                                                            then true
                                                            else
                                                                if code < 42775
                                                                then false
                                                                else
                                                                    if code <= 42783
                                                                    then true
                                                                    else false
                                    else
                                        if code <= 42888
                                        then true
                                        else
                                            if code < 43020
                                            then
                                                if code < 42993
                                                then
                                                    if code < 42891
                                                    then false
                                                    else
                                                        if code <= 42972
                                                        then true
                                                        else false
                                                else
                                                    if code <= 43009
                                                    then true
                                                    else
                                                        if code < 43011
                                                        then false
                                                        else
                                                            if code <= 43013
                                                            then true
                                                            else
                                                                if code < 43015
                                                                then false
                                                                else
                                                                    if code <= 43018
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 43042
                                                then true
                                                else
                                                    if code < 43250
                                                    then
                                                        if code < 43072
                                                        then false
                                                        else
                                                            if code <= 43123
                                                            then true
                                                            else
                                                                if code < 43138
                                                                then false
                                                                else
                                                                    if code <= 43187
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 43255
                                                        then true
                                                        else
                                                            if code < 43259
                                                            then false
                                                            else
                                                                if code <= 43259
                                                                then true
                                                                else
                                                                    if code < 43261
                                                                    then false
                                                                    else
                                                                        if code <= 43262
                                                                        then true
                                                                        else false
                                else
                                    if code <= 43301
                                    then true
                                    else
                                        if code < 43616
                                        then
                                            if code < 43488
                                            then
                                                if code < 43360
                                                then
                                                    if code < 43312
                                                    then false
                                                    else
                                                        if code <= 43334
                                                        then true
                                                        else false
                                                else
                                                    if code <= 43388
                                                    then true
                                                    else
                                                        if code < 43396
                                                        then false
                                                        else
                                                            if code <= 43442
                                                            then true
                                                            else
                                                                if code < 43471
                                                                then false
                                                                else
                                                                    if code <= 43471
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 43492
                                                then true
                                                else
                                                    if code < 43520
                                                    then
                                                        if code < 43494
                                                        then false
                                                        else
                                                            if code <= 43503
                                                            then true
                                                            else
                                                                if code < 43514
                                                                then false
                                                                else
                                                                    if code <= 43518
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 43560
                                                        then true
                                                        else
                                                            if code < 43584
                                                            then false
                                                            else
                                                                if code <= 43586
                                                                then true
                                                                else
                                                                    if code < 43588
                                                                    then false
                                                                    else
                                                                        if code <= 43595
                                                                        then true
                                                                        else false
                                        else
                                            if code <= 43638
                                            then true
                                            else
                                                if code < 43705
                                                then
                                                    if code < 43646
                                                    then
                                                        if code < 43642
                                                        then false
                                                        else
                                                            if code <= 43642
                                                            then true
                                                            else false
                                                    else
                                                        if code <= 43695
                                                        then true
                                                        else
                                                            if code < 43697
                                                            then false
                                                            else
                                                                if code <= 43697
                                                                then true
                                                                else
                                                                    if code < 43701
                                                                    then false
                                                                    else
                                                                        if code <= 43702
                                                                        then true
                                                                        else false
                                                else
                                                    if code <= 43709
                                                    then true
                                                    else
                                                        if code < 43739
                                                        then
                                                            if code < 43712
                                                            then false
                                                            else
                                                                if code <= 43712
                                                                then true
                                                                else
                                                                    if code < 43714
                                                                    then false
                                                                    else
                                                                        if code <= 43714
                                                                        then true
                                                                        else false
                                                        else
                                                            if code <= 43741
                                                            then true
                                                            else
                                                                if code < 43744
                                                                then false
                                                                else
                                                                    if code <= 43754
                                                                    then true
                                                                    else
                                                                        if code < 43762
                                                                        then false
                                                                        else
                                                                            if code <= 43764
                                                                            then true
                                                                            else false
    else
        if code <= 43782
        then true
        else
            if code < 71948
            then
                if code < 67968
                then
                    if code < 65664
                    then
                        if code < 64323
                        then
                            if code < 55243
                            then
                                if code < 43824
                                then
                                    if code < 43793
                                    then
                                        if code < 43785
                                        then false
                                        else
                                            if code <= 43790
                                            then true
                                            else false
                                    else
                                        if code <= 43798
                                        then true
                                        else
                                            if code < 43808
                                            then false
                                            else
                                                if code <= 43814
                                                then true
                                                else
                                                    if code < 43816
                                                    then false
                                                    else
                                                        if code <= 43822
                                                        then true
                                                        else false
                                else
                                    if code <= 43866
                                    then true
                                    else
                                        if code < 43888
                                        then
                                            if code < 43868
                                            then false
                                            else
                                                if code <= 43881
                                                then true
                                                else false
                                        else
                                            if code <= 44002
                                            then true
                                            else
                                                if code < 44032
                                                then false
                                                else
                                                    if code <= 55203
                                                    then true
                                                    else
                                                        if code < 55216
                                                        then false
                                                        else
                                                            if code <= 55238
                                                            then true
                                                            else false
                            else
                                if code <= 55291
                                then true
                                else
                                    if code < 64285
                                    then
                                        if code < 64112
                                        then
                                            if code < 63744
                                            then false
                                            else
                                                if code <= 64109
                                                then true
                                                else false
                                        else
                                            if code <= 64217
                                            then true
                                            else
                                                if code < 64256
                                                then false
                                                else
                                                    if code <= 64262
                                                    then true
                                                    else
                                                        if code < 64275
                                                        then false
                                                        else
                                                            if code <= 64279
                                                            then true
                                                            else false
                                    else
                                        if code <= 64285
                                        then true
                                        else
                                            if code < 64312
                                            then
                                                if code < 64287
                                                then false
                                                else
                                                    if code <= 64296
                                                    then true
                                                    else
                                                        if code < 64298
                                                        then false
                                                        else
                                                            if code <= 64310
                                                            then true
                                                            else false
                                            else
                                                if code <= 64316
                                                then true
                                                else
                                                    if code < 64318
                                                    then false
                                                    else
                                                        if code <= 64318
                                                        then true
                                                        else
                                                            if code < 64320
                                                            then false
                                                            else
                                                                if code <= 64321
                                                                then true
                                                                else false
                        else
                            if code <= 64324
                            then true
                            else
                                if code < 65382
                                then
                                    if code < 65008
                                    then
                                        if code < 64467
                                        then
                                            if code < 64326
                                            then false
                                            else
                                                if code <= 64433
                                                then true
                                                else false
                                        else
                                            if code <= 64829
                                            then true
                                            else
                                                if code < 64848
                                                then false
                                                else
                                                    if code <= 64911
                                                    then true
                                                    else
                                                        if code < 64914
                                                        then false
                                                        else
                                                            if code <= 64967
                                                            then true
                                                            else false
                                    else
                                        if code <= 65019
                                        then true
                                        else
                                            if code < 65142
                                            then
                                                if code < 65136
                                                then false
                                                else
                                                    if code <= 65140
                                                    then true
                                                    else false
                                            else
                                                if code <= 65276
                                                then true
                                                else
                                                    if code < 65313
                                                    then false
                                                    else
                                                        if code <= 65338
                                                        then true
                                                        else
                                                            if code < 65345
                                                            then false
                                                            else
                                                                if code <= 65370
                                                                then true
                                                                else false
                                else
                                    if code <= 65470
                                    then true
                                    else
                                        if code < 65536
                                        then
                                            if code < 65482
                                            then
                                                if code < 65474
                                                then false
                                                else
                                                    if code <= 65479
                                                    then true
                                                    else false
                                            else
                                                if code <= 65487
                                                then true
                                                else
                                                    if code < 65490
                                                    then false
                                                    else
                                                        if code <= 65495
                                                        then true
                                                        else
                                                            if code < 65498
                                                            then false
                                                            else
                                                                if code <= 65500
                                                                then true
                                                                else false
                                        else
                                            if code <= 65547
                                            then true
                                            else
                                                if code < 65596
                                                then
                                                    if code < 65549
                                                    then false
                                                    else
                                                        if code <= 65574
                                                        then true
                                                        else
                                                            if code < 65576
                                                            then false
                                                            else
                                                                if code <= 65594
                                                                then true
                                                                else false
                                                else
                                                    if code <= 65597
                                                    then true
                                                    else
                                                        if code < 65599
                                                        then false
                                                        else
                                                            if code <= 65613
                                                            then true
                                                            else
                                                                if code < 65616
                                                                then false
                                                                else
                                                                    if code <= 65629
                                                                    then true
                                                                    else false
                    else
                        if code <= 65786
                        then true
                        else
                            if code < 66995
                            then
                                if code < 66560
                                then
                                    if code < 66370
                                    then
                                        if code < 66208
                                        then
                                            if code < 66176
                                            then false
                                            else
                                                if code <= 66204
                                                then true
                                                else false
                                        else
                                            if code <= 66256
                                            then true
                                            else
                                                if code < 66304
                                                then false
                                                else
                                                    if code <= 66335
                                                    then true
                                                    else
                                                        if code < 66349
                                                        then false
                                                        else
                                                            if code <= 66368
                                                            then true
                                                            else false
                                    else
                                        if code <= 66377
                                        then true
                                        else
                                            if code < 66432
                                            then
                                                if code < 66384
                                                then false
                                                else
                                                    if code <= 66421
                                                    then true
                                                    else false
                                            else
                                                if code <= 66461
                                                then true
                                                else
                                                    if code < 66464
                                                    then false
                                                    else
                                                        if code <= 66499
                                                        then true
                                                        else
                                                            if code < 66504
                                                            then false
                                                            else
                                                                if code <= 66511
                                                                then true
                                                                else false
                                else
                                    if code <= 66717
                                    then true
                                    else
                                        if code < 66928
                                        then
                                            if code < 66776
                                            then
                                                if code < 66736
                                                then false
                                                else
                                                    if code <= 66771
                                                    then true
                                                    else false
                                            else
                                                if code <= 66811
                                                then true
                                                else
                                                    if code < 66816
                                                    then false
                                                    else
                                                        if code <= 66855
                                                        then true
                                                        else
                                                            if code < 66864
                                                            then false
                                                            else
                                                                if code <= 66915
                                                                then true
                                                                else false
                                        else
                                            if code <= 66938
                                            then true
                                            else
                                                if code < 66964
                                                then
                                                    if code < 66940
                                                    then false
                                                    else
                                                        if code <= 66954
                                                        then true
                                                        else
                                                            if code < 66956
                                                            then false
                                                            else
                                                                if code <= 66962
                                                                then true
                                                                else false
                                                else
                                                    if code <= 66965
                                                    then true
                                                    else
                                                        if code < 66967
                                                        then false
                                                        else
                                                            if code <= 66977
                                                            then true
                                                            else
                                                                if code < 66979
                                                                then false
                                                                else
                                                                    if code <= 66993
                                                                    then true
                                                                    else false
                            else
                                if code <= 67001
                                then true
                                else
                                    if code < 67594
                                    then
                                        if code < 67424
                                        then
                                            if code < 67008
                                            then
                                                if code < 67003
                                                then false
                                                else
                                                    if code <= 67004
                                                    then true
                                                    else false
                                            else
                                                if code <= 67059
                                                then true
                                                else
                                                    if code < 67072
                                                    then false
                                                    else
                                                        if code <= 67382
                                                        then true
                                                        else
                                                            if code < 67392
                                                            then false
                                                            else
                                                                if code <= 67413
                                                                then true
                                                                else false
                                        else
                                            if code <= 67431
                                            then true
                                            else
                                                if code < 67506
                                                then
                                                    if code < 67456
                                                    then false
                                                    else
                                                        if code <= 67461
                                                        then true
                                                        else
                                                            if code < 67463
                                                            then false
                                                            else
                                                                if code <= 67504
                                                                then true
                                                                else false
                                                else
                                                    if code <= 67514
                                                    then true
                                                    else
                                                        if code < 67584
                                                        then false
                                                        else
                                                            if code <= 67589
                                                            then true
                                                            else
                                                                if code < 67592
                                                                then false
                                                                else
                                                                    if code <= 67592
                                                                    then true
                                                                    else false
                                    else
                                        if code <= 67637
                                        then true
                                        else
                                            if code < 67712
                                            then
                                                if code < 67644
                                                then
                                                    if code < 67639
                                                    then false
                                                    else
                                                        if code <= 67640
                                                        then true
                                                        else false
                                                else
                                                    if code <= 67644
                                                    then true
                                                    else
                                                        if code < 67647
                                                        then false
                                                        else
                                                            if code <= 67669
                                                            then true
                                                            else
                                                                if code < 67680
                                                                then false
                                                                else
                                                                    if code <= 67702
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 67742
                                                then true
                                                else
                                                    if code < 67840
                                                    then
                                                        if code < 67808
                                                        then false
                                                        else
                                                            if code <= 67826
                                                            then true
                                                            else
                                                                if code < 67828
                                                                then false
                                                                else
                                                                    if code <= 67829
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 67861
                                                        then true
                                                        else
                                                            if code < 67872
                                                            then false
                                                            else
                                                                if code <= 67897
                                                                then true
                                                                else
                                                                    if code < 67904
                                                                    then false
                                                                    else
                                                                        if code <= 67929
                                                                        then true
                                                                        else false
                else
                    if code <= 68023
                    then true
                    else
                        if code < 70144
                        then
                            if code < 69296
                            then
                                if code < 68352
                                then
                                    if code < 68121
                                    then
                                        if code < 68096
                                        then
                                            if code < 68030
                                            then false
                                            else
                                                if code <= 68031
                                                then true
                                                else false
                                        else
                                            if code <= 68096
                                            then true
                                            else
                                                if code < 68112
                                                then false
                                                else
                                                    if code <= 68115
                                                    then true
                                                    else
                                                        if code < 68117
                                                        then false
                                                        else
                                                            if code <= 68119
                                                            then true
                                                            else false
                                    else
                                        if code <= 68149
                                        then true
                                        else
                                            if code < 68224
                                            then
                                                if code < 68192
                                                then false
                                                else
                                                    if code <= 68220
                                                    then true
                                                    else false
                                            else
                                                if code <= 68252
                                                then true
                                                else
                                                    if code < 68288
                                                    then false
                                                    else
                                                        if code <= 68295
                                                        then true
                                                        else
                                                            if code < 68297
                                                            then false
                                                            else
                                                                if code <= 68324
                                                                then true
                                                                else false
                                else
                                    if code <= 68405
                                    then true
                                    else
                                        if code < 68736
                                        then
                                            if code < 68448
                                            then
                                                if code < 68416
                                                then false
                                                else
                                                    if code <= 68437
                                                    then true
                                                    else false
                                            else
                                                if code <= 68466
                                                then true
                                                else
                                                    if code < 68480
                                                    then false
                                                    else
                                                        if code <= 68497
                                                        then true
                                                        else
                                                            if code < 68608
                                                            then false
                                                            else
                                                                if code <= 68680
                                                                then true
                                                                else false
                                        else
                                            if code <= 68786
                                            then true
                                            else
                                                if code < 68938
                                                then
                                                    if code < 68800
                                                    then false
                                                    else
                                                        if code <= 68850
                                                        then true
                                                        else
                                                            if code < 68864
                                                            then false
                                                            else
                                                                if code <= 68899
                                                                then true
                                                                else false
                                                else
                                                    if code <= 68965
                                                    then true
                                                    else
                                                        if code < 68975
                                                        then false
                                                        else
                                                            if code <= 68997
                                                            then true
                                                            else
                                                                if code < 69248
                                                                then false
                                                                else
                                                                    if code <= 69289
                                                                    then true
                                                                    else false
                            else
                                if code <= 69297
                                then true
                                else
                                    if code < 69763
                                    then
                                        if code < 69488
                                        then
                                            if code < 69376
                                            then
                                                if code < 69314
                                                then false
                                                else
                                                    if code <= 69319
                                                    then true
                                                    else false
                                            else
                                                if code <= 69404
                                                then true
                                                else
                                                    if code < 69415
                                                    then false
                                                    else
                                                        if code <= 69415
                                                        then true
                                                        else
                                                            if code < 69424
                                                            then false
                                                            else
                                                                if code <= 69445
                                                                then true
                                                                else false
                                        else
                                            if code <= 69505
                                            then true
                                            else
                                                if code < 69635
                                                then
                                                    if code < 69552
                                                    then false
                                                    else
                                                        if code <= 69572
                                                        then true
                                                        else
                                                            if code < 69600
                                                            then false
                                                            else
                                                                if code <= 69622
                                                                then true
                                                                else false
                                                else
                                                    if code <= 69687
                                                    then true
                                                    else
                                                        if code < 69745
                                                        then false
                                                        else
                                                            if code <= 69746
                                                            then true
                                                            else
                                                                if code < 69749
                                                                then false
                                                                else
                                                                    if code <= 69749
                                                                    then true
                                                                    else false
                                    else
                                        if code <= 69807
                                        then true
                                        else
                                            if code < 69968
                                            then
                                                if code < 69891
                                                then
                                                    if code < 69840
                                                    then false
                                                    else
                                                        if code <= 69864
                                                        then true
                                                        else false
                                                else
                                                    if code <= 69926
                                                    then true
                                                    else
                                                        if code < 69956
                                                        then false
                                                        else
                                                            if code <= 69956
                                                            then true
                                                            else
                                                                if code < 69959
                                                                then false
                                                                else
                                                                    if code <= 69959
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 70002
                                                then true
                                                else
                                                    if code < 70081
                                                    then
                                                        if code < 70006
                                                        then false
                                                        else
                                                            if code <= 70006
                                                            then true
                                                            else
                                                                if code < 70019
                                                                then false
                                                                else
                                                                    if code <= 70066
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 70084
                                                        then true
                                                        else
                                                            if code < 70106
                                                            then false
                                                            else
                                                                if code <= 70106
                                                                then true
                                                                else
                                                                    if code < 70108
                                                                    then false
                                                                    else
                                                                        if code <= 70108
                                                                        then true
                                                                        else false
                        else
                            if code <= 70161
                            then true
                            else
                                if code < 70544
                                then
                                    if code < 70415
                                    then
                                        if code < 70282
                                        then
                                            if code < 70207
                                            then
                                                if code < 70163
                                                then false
                                                else
                                                    if code <= 70187
                                                    then true
                                                    else false
                                            else
                                                if code <= 70208
                                                then true
                                                else
                                                    if code < 70272
                                                    then false
                                                    else
                                                        if code <= 70278
                                                        then true
                                                        else
                                                            if code < 70280
                                                            then false
                                                            else
                                                                if code <= 70280
                                                                then true
                                                                else false
                                        else
                                            if code <= 70285
                                            then true
                                            else
                                                if code < 70303
                                                then
                                                    if code < 70287
                                                    then false
                                                    else
                                                        if code <= 70301
                                                        then true
                                                        else false
                                                else
                                                    if code <= 70312
                                                    then true
                                                    else
                                                        if code < 70320
                                                        then false
                                                        else
                                                            if code <= 70366
                                                            then true
                                                            else
                                                                if code < 70405
                                                                then false
                                                                else
                                                                    if code <= 70412
                                                                    then true
                                                                    else false
                                    else
                                        if code <= 70416
                                        then true
                                        else
                                            if code < 70461
                                            then
                                                if code < 70442
                                                then
                                                    if code < 70419
                                                    then false
                                                    else
                                                        if code <= 70440
                                                        then true
                                                        else false
                                                else
                                                    if code <= 70448
                                                    then true
                                                    else
                                                        if code < 70450
                                                        then false
                                                        else
                                                            if code <= 70451
                                                            then true
                                                            else
                                                                if code < 70453
                                                                then false
                                                                else
                                                                    if code <= 70457
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 70461
                                                then true
                                                else
                                                    if code < 70528
                                                    then
                                                        if code < 70480
                                                        then false
                                                        else
                                                            if code <= 70480
                                                            then true
                                                            else
                                                                if code < 70493
                                                                then false
                                                                else
                                                                    if code <= 70497
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 70537
                                                        then true
                                                        else
                                                            if code < 70539
                                                            then false
                                                            else
                                                                if code <= 70539
                                                                then true
                                                                else
                                                                    if code < 70542
                                                                    then false
                                                                    else
                                                                        if code <= 70542
                                                                        then true
                                                                        else false
                                else
                                    if code <= 70581
                                    then true
                                    else
                                        if code < 71128
                                        then
                                            if code < 70727
                                            then
                                                if code < 70609
                                                then
                                                    if code < 70583
                                                    then false
                                                    else
                                                        if code <= 70583
                                                        then true
                                                        else false
                                                else
                                                    if code <= 70609
                                                    then true
                                                    else
                                                        if code < 70611
                                                        then false
                                                        else
                                                            if code <= 70611
                                                            then true
                                                            else
                                                                if code < 70656
                                                                then false
                                                                else
                                                                    if code <= 70708
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 70730
                                                then true
                                                else
                                                    if code < 70852
                                                    then
                                                        if code < 70751
                                                        then false
                                                        else
                                                            if code <= 70753
                                                            then true
                                                            else
                                                                if code < 70784
                                                                then false
                                                                else
                                                                    if code <= 70831
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 70853
                                                        then true
                                                        else
                                                            if code < 70855
                                                            then false
                                                            else
                                                                if code <= 70855
                                                                then true
                                                                else
                                                                    if code < 71040
                                                                    then false
                                                                    else
                                                                        if code <= 71086
                                                                        then true
                                                                        else false
                                        else
                                            if code <= 71131
                                            then true
                                            else
                                                if code < 71424
                                                then
                                                    if code < 71236
                                                    then
                                                        if code < 71168
                                                        then false
                                                        else
                                                            if code <= 71215
                                                            then true
                                                            else false
                                                    else
                                                        if code <= 71236
                                                        then true
                                                        else
                                                            if code < 71296
                                                            then false
                                                            else
                                                                if code <= 71338
                                                                then true
                                                                else
                                                                    if code < 71352
                                                                    then false
                                                                    else
                                                                        if code <= 71352
                                                                        then true
                                                                        else false
                                                else
                                                    if code <= 71450
                                                    then true
                                                    else
                                                        if code < 71840
                                                        then
                                                            if code < 71488
                                                            then false
                                                            else
                                                                if code <= 71494
                                                                then true
                                                                else
                                                                    if code < 71680
                                                                    then false
                                                                    else
                                                                        if code <= 71723
                                                                        then true
                                                                        else false
                                                        else
                                                            if code <= 71903
                                                            then true
                                                            else
                                                                if code < 71935
                                                                then false
                                                                else
                                                                    if code <= 71942
                                                                    then true
                                                                    else
                                                                        if code < 71945
                                                                        then false
                                                                        else
                                                                            if code <= 71945
                                                                            then true
                                                                            else false
            else
                if code <= 71955
                then true
                else
                    if code < 120005
                    then
                        if code < 92160
                        then
                            if code < 72960
                            then
                                if code < 72203
                                then
                                    if code < 72096
                                    then
                                        if code < 71960
                                        then
                                            if code < 71957
                                            then false
                                            else
                                                if code <= 71958
                                                then true
                                                else false
                                        else
                                            if code <= 71983
                                            then true
                                            else
                                                if code < 71999
                                                then false
                                                else
                                                    if code <= 71999
                                                    then true
                                                    else
                                                        if code < 72001
                                                        then false
                                                        else
                                                            if code <= 72001
                                                            then true
                                                            else false
                                    else
                                        if code <= 72103
                                        then true
                                        else
                                            if code < 72161
                                            then
                                                if code < 72106
                                                then false
                                                else
                                                    if code <= 72144
                                                    then true
                                                    else false
                                            else
                                                if code <= 72161
                                                then true
                                                else
                                                    if code < 72163
                                                    then false
                                                    else
                                                        if code <= 72163
                                                        then true
                                                        else
                                                            if code < 72192
                                                            then false
                                                            else
                                                                if code <= 72192
                                                                then true
                                                                else false
                                else
                                    if code <= 72242
                                    then true
                                    else
                                        if code < 72368
                                        then
                                            if code < 72272
                                            then
                                                if code < 72250
                                                then false
                                                else
                                                    if code <= 72250
                                                    then true
                                                    else false
                                            else
                                                if code <= 72272
                                                then true
                                                else
                                                    if code < 72284
                                                    then false
                                                    else
                                                        if code <= 72329
                                                        then true
                                                        else
                                                            if code < 72349
                                                            then false
                                                            else
                                                                if code <= 72349
                                                                then true
                                                                else false
                                        else
                                            if code <= 72440
                                            then true
                                            else
                                                if code < 72714
                                                then
                                                    if code < 72640
                                                    then false
                                                    else
                                                        if code <= 72672
                                                        then true
                                                        else
                                                            if code < 72704
                                                            then false
                                                            else
                                                                if code <= 72712
                                                                then true
                                                                else false
                                                else
                                                    if code <= 72750
                                                    then true
                                                    else
                                                        if code < 72768
                                                        then false
                                                        else
                                                            if code <= 72768
                                                            then true
                                                            else
                                                                if code < 72818
                                                                then false
                                                                else
                                                                    if code <= 72847
                                                                    then true
                                                                    else false
                            else
                                if code <= 72966
                                then true
                                else
                                    if code < 73476
                                    then
                                        if code < 73063
                                        then
                                            if code < 72971
                                            then
                                                if code < 72968
                                                then false
                                                else
                                                    if code <= 72969
                                                    then true
                                                    else false
                                            else
                                                if code <= 73008
                                                then true
                                                else
                                                    if code < 73030
                                                    then false
                                                    else
                                                        if code <= 73030
                                                        then true
                                                        else
                                                            if code < 73056
                                                            then false
                                                            else
                                                                if code <= 73061
                                                                then true
                                                                else false
                                        else
                                            if code <= 73064
                                            then true
                                            else
                                                if code < 73136
                                                then
                                                    if code < 73066
                                                    then false
                                                    else
                                                        if code <= 73097
                                                        then true
                                                        else
                                                            if code < 73112
                                                            then false
                                                            else
                                                                if code <= 73112
                                                                then true
                                                                else false
                                                else
                                                    if code <= 73179
                                                    then true
                                                    else
                                                        if code < 73440
                                                        then false
                                                        else
                                                            if code <= 73458
                                                            then true
                                                            else
                                                                if code < 73474
                                                                then false
                                                                else
                                                                    if code <= 73474
                                                                    then true
                                                                    else false
                                    else
                                        if code <= 73488
                                        then true
                                        else
                                            if code < 77712
                                            then
                                                if code < 73648
                                                then
                                                    if code < 73490
                                                    then false
                                                    else
                                                        if code <= 73523
                                                        then true
                                                        else false
                                                else
                                                    if code <= 73648
                                                    then true
                                                    else
                                                        if code < 73728
                                                        then false
                                                        else
                                                            if code <= 74649
                                                            then true
                                                            else
                                                                if code < 74880
                                                                then false
                                                                else
                                                                    if code <= 75075
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 77808
                                                then true
                                                else
                                                    if code < 78944
                                                    then
                                                        if code < 77824
                                                        then false
                                                        else
                                                            if code <= 78895
                                                            then true
                                                            else
                                                                if code < 78913
                                                                then false
                                                                else
                                                                    if code <= 78918
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 82938
                                                        then true
                                                        else
                                                            if code < 82944
                                                            then false
                                                            else
                                                                if code <= 83526
                                                                then true
                                                                else
                                                                    if code < 90368
                                                                    then false
                                                                    else
                                                                        if code <= 90397
                                                                        then true
                                                                        else false
                        else
                            if code <= 92728
                            then true
                            else
                                if code < 110576
                                then
                                    if code < 93856
                                    then
                                        if code < 92992
                                        then
                                            if code < 92784
                                            then
                                                if code < 92736
                                                then false
                                                else
                                                    if code <= 92766
                                                    then true
                                                    else false
                                            else
                                                if code <= 92862
                                                then true
                                                else
                                                    if code < 92880
                                                    then false
                                                    else
                                                        if code <= 92909
                                                        then true
                                                        else
                                                            if code < 92928
                                                            then false
                                                            else
                                                                if code <= 92975
                                                                then true
                                                                else false
                                        else
                                            if code <= 92995
                                            then true
                                            else
                                                if code < 93053
                                                then
                                                    if code < 93027
                                                    then false
                                                    else
                                                        if code <= 93047
                                                        then true
                                                        else false
                                                else
                                                    if code <= 93071
                                                    then true
                                                    else
                                                        if code < 93504
                                                        then false
                                                        else
                                                            if code <= 93548
                                                            then true
                                                            else
                                                                if code < 93760
                                                                then false
                                                                else
                                                                    if code <= 93823
                                                                    then true
                                                                    else false
                                    else
                                        if code <= 93880
                                        then true
                                        else
                                            if code < 94176
                                            then
                                                if code < 93952
                                                then
                                                    if code < 93883
                                                    then false
                                                    else
                                                        if code <= 93907
                                                        then true
                                                        else false
                                                else
                                                    if code <= 94026
                                                    then true
                                                    else
                                                        if code < 94032
                                                        then false
                                                        else
                                                            if code <= 94032
                                                            then true
                                                            else
                                                                if code < 94099
                                                                then false
                                                                else
                                                                    if code <= 94111
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 94177
                                                then true
                                                else
                                                    if code < 94208
                                                    then
                                                        if code < 94179
                                                        then false
                                                        else
                                                            if code <= 94179
                                                            then true
                                                            else
                                                                if code < 94194
                                                                then false
                                                                else
                                                                    if code <= 94195
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 101589
                                                        then true
                                                        else
                                                            if code < 101631
                                                            then false
                                                            else
                                                                if code <= 101662
                                                                then true
                                                                else
                                                                    if code < 101760
                                                                    then false
                                                                    else
                                                                        if code <= 101874
                                                                        then true
                                                                        else false
                                else
                                    if code <= 110579
                                    then true
                                    else
                                        if code < 113792
                                        then
                                            if code < 110928
                                            then
                                                if code < 110589
                                                then
                                                    if code < 110581
                                                    then false
                                                    else
                                                        if code <= 110587
                                                        then true
                                                        else false
                                                else
                                                    if code <= 110590
                                                    then true
                                                    else
                                                        if code < 110592
                                                        then false
                                                        else
                                                            if code <= 110882
                                                            then true
                                                            else
                                                                if code < 110898
                                                                then false
                                                                else
                                                                    if code <= 110898
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 110930
                                                then true
                                                else
                                                    if code < 110960
                                                    then
                                                        if code < 110933
                                                        then false
                                                        else
                                                            if code <= 110933
                                                            then true
                                                            else
                                                                if code < 110948
                                                                then false
                                                                else
                                                                    if code <= 110951
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 111355
                                                        then true
                                                        else
                                                            if code < 113664
                                                            then false
                                                            else
                                                                if code <= 113770
                                                                then true
                                                                else
                                                                    if code < 113776
                                                                    then false
                                                                    else
                                                                        if code <= 113788
                                                                        then true
                                                                        else false
                                        else
                                            if code <= 113800
                                            then true
                                            else
                                                if code < 119970
                                                then
                                                    if code < 119808
                                                    then
                                                        if code < 113808
                                                        then false
                                                        else
                                                            if code <= 113817
                                                            then true
                                                            else false
                                                    else
                                                        if code <= 119892
                                                        then true
                                                        else
                                                            if code < 119894
                                                            then false
                                                            else
                                                                if code <= 119964
                                                                then true
                                                                else
                                                                    if code < 119966
                                                                    then false
                                                                    else
                                                                        if code <= 119967
                                                                        then true
                                                                        else false
                                                else
                                                    if code <= 119970
                                                    then true
                                                    else
                                                        if code < 119982
                                                        then
                                                            if code < 119973
                                                            then false
                                                            else
                                                                if code <= 119974
                                                                then true
                                                                else
                                                                    if code < 119977
                                                                    then false
                                                                    else
                                                                        if code <= 119980
                                                                        then true
                                                                        else false
                                                        else
                                                            if code <= 119993
                                                            then true
                                                            else
                                                                if code < 119995
                                                                then false
                                                                else
                                                                    if code <= 119995
                                                                    then true
                                                                    else
                                                                        if code < 119997
                                                                        then false
                                                                        else
                                                                            if code <= 120003
                                                                            then true
                                                                            else false
                    else
                        if code <= 120069
                        then true
                        else
                            if code < 125184
                            then
                                if code < 122624
                                then
                                    if code < 120488
                                    then
                                        if code < 120123
                                        then
                                            if code < 120077
                                            then
                                                if code < 120071
                                                then false
                                                else
                                                    if code <= 120074
                                                    then true
                                                    else false
                                            else
                                                if code <= 120084
                                                then true
                                                else
                                                    if code < 120086
                                                    then false
                                                    else
                                                        if code <= 120092
                                                        then true
                                                        else
                                                            if code < 120094
                                                            then false
                                                            else
                                                                if code <= 120121
                                                                then true
                                                                else false
                                        else
                                            if code <= 120126
                                            then true
                                            else
                                                if code < 120134
                                                then
                                                    if code < 120128
                                                    then false
                                                    else
                                                        if code <= 120132
                                                        then true
                                                        else false
                                                else
                                                    if code <= 120134
                                                    then true
                                                    else
                                                        if code < 120138
                                                        then false
                                                        else
                                                            if code <= 120144
                                                            then true
                                                            else
                                                                if code < 120146
                                                                then false
                                                                else
                                                                    if code <= 120485
                                                                    then true
                                                                    else false
                                    else
                                        if code <= 120512
                                        then true
                                        else
                                            if code < 120630
                                            then
                                                if code < 120540
                                                then
                                                    if code < 120514
                                                    then false
                                                    else
                                                        if code <= 120538
                                                        then true
                                                        else false
                                                else
                                                    if code <= 120570
                                                    then true
                                                    else
                                                        if code < 120572
                                                        then false
                                                        else
                                                            if code <= 120596
                                                            then true
                                                            else
                                                                if code < 120598
                                                                then false
                                                                else
                                                                    if code <= 120628
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 120654
                                                then true
                                                else
                                                    if code < 120714
                                                    then
                                                        if code < 120656
                                                        then false
                                                        else
                                                            if code <= 120686
                                                            then true
                                                            else
                                                                if code < 120688
                                                                then false
                                                                else
                                                                    if code <= 120712
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 120744
                                                        then true
                                                        else
                                                            if code < 120746
                                                            then false
                                                            else
                                                                if code <= 120770
                                                                then true
                                                                else
                                                                    if code < 120772
                                                                    then false
                                                                    else
                                                                        if code <= 120779
                                                                        then true
                                                                        else false
                                else
                                    if code <= 122654
                                    then true
                                    else
                                        if code < 124608
                                        then
                                            if code < 123214
                                            then
                                                if code < 122928
                                                then
                                                    if code < 122661
                                                    then false
                                                    else
                                                        if code <= 122666
                                                        then true
                                                        else false
                                                else
                                                    if code <= 122989
                                                    then true
                                                    else
                                                        if code < 123136
                                                        then false
                                                        else
                                                            if code <= 123180
                                                            then true
                                                            else
                                                                if code < 123191
                                                                then false
                                                                else
                                                                    if code <= 123197
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 123214
                                                then true
                                                else
                                                    if code < 124112
                                                    then
                                                        if code < 123536
                                                        then false
                                                        else
                                                            if code <= 123565
                                                            then true
                                                            else
                                                                if code < 123584
                                                                then false
                                                                else
                                                                    if code <= 123627
                                                                    then true
                                                                    else false
                                                    else
                                                        if code <= 124139
                                                        then true
                                                        else
                                                            if code < 124368
                                                            then false
                                                            else
                                                                if code <= 124397
                                                                then true
                                                                else
                                                                    if code < 124400
                                                                    then false
                                                                    else
                                                                        if code <= 124400
                                                                        then true
                                                                        else false
                                        else
                                            if code <= 124638
                                            then true
                                            else
                                                if code < 124670
                                                then
                                                    if code < 124644
                                                    then
                                                        if code < 124640
                                                        then false
                                                        else
                                                            if code <= 124642
                                                            then true
                                                            else false
                                                    else
                                                        if code <= 124645
                                                        then true
                                                        else
                                                            if code < 124647
                                                            then false
                                                            else
                                                                if code <= 124653
                                                                then true
                                                                else
                                                                    if code < 124656
                                                                    then false
                                                                    else
                                                                        if code <= 124660
                                                                        then true
                                                                        else false
                                                else
                                                    if code <= 124671
                                                    then true
                                                    else
                                                        if code < 124909
                                                        then
                                                            if code < 124896
                                                            then false
                                                            else
                                                                if code <= 124902
                                                                then true
                                                                else
                                                                    if code < 124904
                                                                    then false
                                                                    else
                                                                        if code <= 124907
                                                                        then true
                                                                        else false
                                                        else
                                                            if code <= 124910
                                                            then true
                                                            else
                                                                if code < 124912
                                                                then false
                                                                else
                                                                    if code <= 124926
                                                                    then true
                                                                    else
                                                                        if code < 124928
                                                                        then false
                                                                        else
                                                                            if code <= 125124
                                                                            then true
                                                                            else false
                            else
                                if code <= 125251
                                then true
                                else
                                    if code < 126557
                                    then
                                        if code < 126523
                                        then
                                            if code < 126500
                                            then
                                                if code < 126464
                                                then
                                                    if code < 125259
                                                    then false
                                                    else
                                                        if code <= 125259
                                                        then true
                                                        else false
                                                else
                                                    if code <= 126467
                                                    then true
                                                    else
                                                        if code < 126469
                                                        then false
                                                        else
                                                            if code <= 126495
                                                            then true
                                                            else
                                                                if code < 126497
                                                                then false
                                                                else
                                                                    if code <= 126498
                                                                    then true
                                                                    else false
                                            else
                                                if code <= 126500
                                                then true
                                                else
                                                    if code < 126505
                                                    then
                                                        if code < 126503
                                                        then false
                                                        else
                                                            if code <= 126503
                                                            then true
                                                            else false
                                                    else
                                                        if code <= 126514
                                                        then true
                                                        else
                                                            if code < 126516
                                                            then false
                                                            else
                                                                if code <= 126519
                                                                then true
                                                                else
                                                                    if code < 126521
                                                                    then false
                                                                    else
                                                                        if code <= 126521
                                                                        then true
                                                                        else false
                                        else
                                            if code <= 126523
                                            then true
                                            else
                                                if code < 126541
                                                then
                                                    if code < 126535
                                                    then
                                                        if code < 126530
                                                        then false
                                                        else
                                                            if code <= 126530
                                                            then true
                                                            else false
                                                    else
                                                        if code <= 126535
                                                        then true
                                                        else
                                                            if code < 126537
                                                            then false
                                                            else
                                                                if code <= 126537
                                                                then true
                                                                else
                                                                    if code < 126539
                                                                    then false
                                                                    else
                                                                        if code <= 126539
                                                                        then true
                                                                        else false
                                                else
                                                    if code <= 126543
                                                    then true
                                                    else
                                                        if code < 126551
                                                        then
                                                            if code < 126545
                                                            then false
                                                            else
                                                                if code <= 126546
                                                                then true
                                                                else
                                                                    if code < 126548
                                                                    then false
                                                                    else
                                                                        if code <= 126548
                                                                        then true
                                                                        else false
                                                        else
                                                            if code <= 126551
                                                            then true
                                                            else
                                                                if code < 126553
                                                                then false
                                                                else
                                                                    if code <= 126553
                                                                    then true
                                                                    else
                                                                        if code < 126555
                                                                        then false
                                                                        else
                                                                            if code <= 126555
                                                                            then true
                                                                            else false
                                    else
                                        if code <= 126557
                                        then true
                                        else
                                            if code < 126625
                                            then
                                                if code < 126572
                                                then
                                                    if code < 126561
                                                    then
                                                        if code < 126559
                                                        then false
                                                        else
                                                            if code <= 126559
                                                            then true
                                                            else false
                                                    else
                                                        if code <= 126562
                                                        then true
                                                        else
                                                            if code < 126564
                                                            then false
                                                            else
                                                                if code <= 126564
                                                                then true
                                                                else
                                                                    if code < 126567
                                                                    then false
                                                                    else
                                                                        if code <= 126570
                                                                        then true
                                                                        else false
                                                else
                                                    if code <= 126578
                                                    then true
                                                    else
                                                        if code < 126590
                                                        then
                                                            if code < 126580
                                                            then false
                                                            else
                                                                if code <= 126583
                                                                then true
                                                                else
                                                                    if code < 126585
                                                                    then false
                                                                    else
                                                                        if code <= 126588
                                                                        then true
                                                                        else false
                                                        else
                                                            if code <= 126590
                                                            then true
                                                            else
                                                                if code < 126592
                                                                then false
                                                                else
                                                                    if code <= 126601
                                                                    then true
                                                                    else
                                                                        if code < 126603
                                                                        then false
                                                                        else
                                                                            if code <= 126619
                                                                            then true
                                                                            else false
                                            else
                                                if code <= 126627
                                                then true
                                                else
                                                    if code < 178208
                                                    then
                                                        if code < 126635
                                                        then
                                                            if code < 126629
                                                            then false
                                                            else
                                                                if code <= 126633
                                                                then true
                                                                else false
                                                        else
                                                            if code <= 126651
                                                            then true
                                                            else
                                                                if code < 131072
                                                                then false
                                                                else
                                                                    if code <= 173791
                                                                    then true
                                                                    else
                                                                        if code < 173824
                                                                        then false
                                                                        else
                                                                            if code <= 178205
                                                                            then true
                                                                            else false
                                                    else
                                                        if code <= 183981
                                                        then true
                                                        else
                                                            if code < 194560
                                                            then
                                                                if code < 183984
                                                                then false
                                                                else
                                                                    if code <= 191456
                                                                    then true
                                                                    else
                                                                        if code < 191472
                                                                        then false
                                                                        else
                                                                            if code <= 192093
                                                                            then true
                                                                            else false
                                                            else
                                                                if code <= 195101
                                                                then true
                                                                else
                                                                    if code < 196608
                                                                    then false
                                                                    else
                                                                        if code <= 201546
                                                                        then true
                                                                        else
                                                                            if code < 201552
                                                                            then false
                                                                            else
                                                                                if code <= 210041
                                                                                then true
                                                                                else false

let isUnicodeDecimalDigit (code: Int) =
    if code < 44016
    then
        if code < 4160
        then
            if code < 2918
            then
                if code < 1984
                then
                    if code < 1632
                    then
                        if code < 48
                        then false
                        else
                            if code <= 57
                            then true
                            else false
                    else
                        if code <= 1641
                        then true
                        else
                            if code < 1776
                            then false
                            else
                                if code <= 1785
                                then true
                                else false
                else
                    if code <= 1993
                    then true
                    else
                        if code < 2534
                        then
                            if code < 2406
                            then false
                            else
                                if code <= 2415
                                then true
                                else false
                        else
                            if code <= 2543
                            then true
                            else
                                if code < 2662
                                then false
                                else
                                    if code <= 2671
                                    then true
                                    else
                                        if code < 2790
                                        then false
                                        else
                                            if code <= 2799
                                            then true
                                            else false
            else
                if code <= 2927
                then true
                else
                    if code < 3430
                    then
                        if code < 3174
                        then
                            if code < 3046
                            then false
                            else
                                if code <= 3055
                                then true
                                else false
                        else
                            if code <= 3183
                            then true
                            else
                                if code < 3302
                                then false
                                else
                                    if code <= 3311
                                    then true
                                    else false
                    else
                        if code <= 3439
                        then true
                        else
                            if code < 3664
                            then
                                if code < 3558
                                then false
                                else
                                    if code <= 3567
                                    then true
                                    else false
                            else
                                if code <= 3673
                                then true
                                else
                                    if code < 3792
                                    then false
                                    else
                                        if code <= 3801
                                        then true
                                        else
                                            if code < 3872
                                            then false
                                            else
                                                if code <= 3881
                                                then true
                                                else false
        else
            if code <= 4169
            then true
            else
                if code < 7088
                then
                    if code < 6470
                    then
                        if code < 6112
                        then
                            if code < 4240
                            then false
                            else
                                if code <= 4249
                                then true
                                else false
                        else
                            if code <= 6121
                            then true
                            else
                                if code < 6160
                                then false
                                else
                                    if code <= 6169
                                    then true
                                    else false
                    else
                        if code <= 6479
                        then true
                        else
                            if code < 6784
                            then
                                if code < 6608
                                then false
                                else
                                    if code <= 6617
                                    then true
                                    else false
                            else
                                if code <= 6793
                                then true
                                else
                                    if code < 6800
                                    then false
                                    else
                                        if code <= 6809
                                        then true
                                        else
                                            if code < 6992
                                            then false
                                            else
                                                if code <= 7001
                                                then true
                                                else false
                else
                    if code <= 7097
                    then true
                    else
                        if code < 43216
                        then
                            if code < 7248
                            then
                                if code < 7232
                                then false
                                else
                                    if code <= 7241
                                    then true
                                    else false
                            else
                                if code <= 7257
                                then true
                                else
                                    if code < 42528
                                    then false
                                    else
                                        if code <= 42537
                                        then true
                                        else false
                        else
                            if code <= 43225
                            then true
                            else
                                if code < 43472
                                then
                                    if code < 43264
                                    then false
                                    else
                                        if code <= 43273
                                        then true
                                        else false
                                else
                                    if code <= 43481
                                    then true
                                    else
                                        if code < 43504
                                        then false
                                        else
                                            if code <= 43513
                                            then true
                                            else
                                                if code < 43600
                                                then false
                                                else
                                                    if code <= 43609
                                                    then true
                                                    else false
    else
        if code <= 44025
        then true
        else
            if code < 72688
            then
                if code < 70384
                then
                    if code < 68928
                    then
                        if code < 66720
                        then
                            if code < 65296
                            then false
                            else
                                if code <= 65305
                                then true
                                else false
                        else
                            if code <= 66729
                            then true
                            else
                                if code < 68912
                                then false
                                else
                                    if code <= 68921
                                    then true
                                    else false
                    else
                        if code <= 68937
                        then true
                        else
                            if code < 69872
                            then
                                if code < 69734
                                then false
                                else
                                    if code <= 69743
                                    then true
                                    else false
                            else
                                if code <= 69881
                                then true
                                else
                                    if code < 69942
                                    then false
                                    else
                                        if code <= 69951
                                        then true
                                        else
                                            if code < 70096
                                            then false
                                            else
                                                if code <= 70105
                                                then true
                                                else false
                else
                    if code <= 70393
                    then true
                    else
                        if code < 71360
                        then
                            if code < 70864
                            then
                                if code < 70736
                                then false
                                else
                                    if code <= 70745
                                    then true
                                    else false
                            else
                                if code <= 70873
                                then true
                                else
                                    if code < 71248
                                    then false
                                    else
                                        if code <= 71257
                                        then true
                                        else false
                        else
                            if code <= 71369
                            then true
                            else
                                if code < 71472
                                then
                                    if code < 71376
                                    then false
                                    else
                                        if code <= 71395
                                        then true
                                        else false
                                else
                                    if code <= 71481
                                    then true
                                    else
                                        if code < 71904
                                        then false
                                        else
                                            if code <= 71913
                                            then true
                                            else
                                                if code < 72016
                                                then false
                                                else
                                                    if code <= 72025
                                                    then true
                                                    else false
            else
                if code <= 72697
                then true
                else
                    if code < 93008
                    then
                        if code < 73184
                        then
                            if code < 73040
                            then
                                if code < 72784
                                then false
                                else
                                    if code <= 72793
                                    then true
                                    else false
                            else
                                if code <= 73049
                                then true
                                else
                                    if code < 73120
                                    then false
                                    else
                                        if code <= 73129
                                        then true
                                        else false
                        else
                            if code <= 73193
                            then true
                            else
                                if code < 90416
                                then
                                    if code < 73552
                                    then false
                                    else
                                        if code <= 73561
                                        then true
                                        else false
                                else
                                    if code <= 90425
                                    then true
                                    else
                                        if code < 92768
                                        then false
                                        else
                                            if code <= 92777
                                            then true
                                            else
                                                if code < 92864
                                                then false
                                                else
                                                    if code <= 92873
                                                    then true
                                                    else false
                    else
                        if code <= 93017
                        then true
                        else
                            if code < 123632
                            then
                                if code < 118000
                                then
                                    if code < 93552
                                    then false
                                    else
                                        if code <= 93561
                                        then true
                                        else false
                                else
                                    if code <= 118009
                                    then true
                                    else
                                        if code < 120782
                                        then false
                                        else
                                            if code <= 120831
                                            then true
                                            else
                                                if code < 123200
                                                then false
                                                else
                                                    if code <= 123209
                                                    then true
                                                    else false
                            else
                                if code <= 123641
                                then true
                                else
                                    if code < 124401
                                    then
                                        if code < 124144
                                        then false
                                        else
                                            if code <= 124153
                                            then true
                                            else false
                                    else
                                        if code <= 124410
                                        then true
                                        else
                                            if code < 125264
                                            then false
                                            else
                                                if code <= 125273
                                                then true
                                                else
                                                    if code < 130032
                                                    then false
                                                    else
                                                        if code <= 130041
                                                        then true
                                                        else false

let isUnicodeWhiteSpace (code: Int) =
    if code < 6158
    then
        if code < 133
        then
            if code < 9
            then false
            else
                if code <= 13
                then true
                else
                    if code < 32
                    then false
                    else
                        if code <= 32
                        then true
                        else false
        else
            if code <= 133
            then true
            else
                if code < 160
                then false
                else
                    if code <= 160
                    then true
                    else
                        if code < 5760
                        then false
                        else
                            if code <= 5760
                            then true
                            else false
    else
        if code <= 6158
        then true
        else
            if code < 8239
            then
                if code < 8192
                then false
                else
                    if code <= 8202
                    then true
                    else
                        if code < 8232
                        then false
                        else
                            if code <= 8233
                            then true
                            else false
            else
                if code <= 8239
                then true
                else
                    if code < 8287
                    then false
                    else
                        if code <= 8287
                        then true
                        else
                            if code < 12288
                            then false
                            else
                                if code <= 12288
                                then true
                                else false

let isUnicodeIdentifierCharacter (code: Int) = isUnicodeLetter(code) || isUnicodeDecimalDigit(code)
