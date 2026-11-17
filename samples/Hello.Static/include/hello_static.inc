; hello_static.inc - public interface of libhellostatic (Hello.Static sample).
; Included by consumers through the project reference or the package, which add
; this folder to their include path.
%ifndef HELLO_STATIC_INC
%define HELLO_STATIC_INC

; static_mul(6, 7) returns this; consumers check it.
%define HELLO_STATIC_MAGIC 42

; int static_mul(int a, int b) - System V ABI.
extern static_mul

%endif
