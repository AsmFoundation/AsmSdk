; libconly.inc - public interface of libtestlibconly (LibcPackage test project),
; included by consumers through the package's include/ folder.
%ifndef LIBCONLY_INC
%define LIBCONLY_INC

; size_t libconly_length(const char *text) - strlen(text), through glibc.
extern libconly_length

%endif
