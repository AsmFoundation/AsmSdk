; old.asm - a source for the OldNames test project, which must fail before it
; is assembled: if this file is ever assembled, the removed-setting checks did
; not stop the build, and the %error below makes that visible.
%error "OldNames was assembled: the ASM0100-ASM0108 checks did not stop the build"
