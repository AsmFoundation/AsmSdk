/* qamath.h - the C interface of QaMath (the interop tour), for C callers and as the contract the C#
   declarations follow. Every function uses the platform's C calling convention. */
#ifndef QAMATH_H
#define QAMATH_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif

typedef struct vec3i { int32_t x, y, z; } vec3i;
typedef struct pair { int32_t a, b; } pair;
typedef struct quad { int64_t x, y; } quad;

int32_t qa_add(int32_t a, int32_t b);
int64_t qa_sum_i64(const int64_t *values, size_t count);
double qa_mix(int64_t a, double b, int64_t c, double d);
int64_t qa_many(int64_t a1, int64_t a2, int64_t a3, int64_t a4, int64_t a5, int64_t a6, int64_t a7, int64_t a8);
int64_t qa_dot3(const vec3i *a, const vec3i *b);
int64_t qa_pair_sum(pair p);
pair qa_make_pair(int32_t a, int32_t b);
quad qa_make_quad(int64_t x, int64_t y);
int64_t qa_reduce(const int32_t *values, size_t count, int64_t (*op)(int64_t acc, int32_t value));
const char *qa_version(void);
int64_t qa_utf8_count(const char *s);
size_t qa_upper_ascii(char *s, size_t length);
int32_t qa_divmod(int32_t a, int32_t b, int32_t *quotient, int32_t *remainder);
bool qa_is_even(int64_t x);
int64_t qa_counter_add(int64_t delta);
void qa_add_f32(const float *a, const float *b, float *sum, size_t count);
int64_t qa_sum_squares(const int64_t *values, size_t count);

#ifdef __cplusplus
}
#endif
#endif
