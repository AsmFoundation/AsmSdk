/*
 * main.c: a C program that calls the asm library ApiStruct through the C header the SDK writes from
 * its PROTO and STRUCT lines, apistruct.h (CHeaderTests, where a C compiler is on PATH: the Linux
 * and macOS CI machines). Every kind of struct field is filled here in C and read by the asm code
 * at the offset the SDK gave it, so a line comes out right only where C lays the field out the same
 * way (and the header's layout checks stop the compile before that). It prints what NetStructs, the
 * C# side, prints:
 *   rect_area 15 / point_set 40 2 / state_sum 144 / sorter_call 42 / mixed_get 42 /
 *   sizes 8=8 20=20 112=112 24=24 24=24
 */
#include <stdio.h>
#include "apistruct.h"

/* The callback the asm code calls through sorter.cmp: a + b, in the platform's C convention. */
static int32_t add(int32_t a, int32_t b)
{
    return a + b;
}

int main(void)
{
    /* A struct holding two structs by value: rect.max + point.y is offset 12. */
    rect area = { { 1, 2 }, { 4, 7 }, 0 };
    printf("rect_area %d\n", (int)rect_area(&area));

    /* Written by the asm code, read here. */
    point p = { 0, 0 };
    point_set(&p, 40, 2);
    printf("point_set %d %d\n", (int)p.x, (int)p.y);

    /* Arrays and a bool: 1 + ... + 8 + 100 + 7 + 1. */
    hash_state hash = { { 0 }, 0, { 0 }, false };
    for (int i = 0; i < 8; i++)
    {
        hash.h[i] = (uint32_t)(i + 1);
    }

    hash.length = 100;
    hash.block[63] = 7;
    hash.final = true;
    printf("state_sum %llu\n", (unsigned long long)state_sum(&hash));

    /* A function pointer field, called by the asm code: 40 + 2. */
    sorter s = { add, NULL, NULL };
    s.next = &s;
    printf("sorter_call %d\n", (int)sorter_call(&s, 40, 2));

    /* A bool, a pointer after padding, a char: 30 + 6 + 5 + 1. */
    uint8_t data[1] = { 5 };
    mixed m = { true, data, 30, 6, 1 };
    printf("mixed_get %u\n", (unsigned)mixed_get(&m));

    /* The asm code's size constants next to C's sizeof, struct by struct. */
    size_t sizes[5];
    size_t count = struct_sizes(sizes);
    size_t c_sizes[5] = { sizeof(point), sizeof(rect), sizeof(hash_state), sizeof(sorter), sizeof(mixed) };
    printf("sizes");
    for (size_t i = 0; i < count && i < 5; i++)
    {
        printf(" %zu=%zu", sizes[i], c_sizes[i]);
    }

    printf("\n");
    return 0;
}
