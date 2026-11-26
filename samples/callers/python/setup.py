"""Tags the wheel for the platform it is built on.

The package itself is pure Python, but the wheel carries a native library built for one
platform, so it must not be tagged py3-none-any: pip would install a Linux library on Windows.
This wheel is py3-none-<platform> instead (for example py3-none-linux_x86_64): any CPython 3,
because ctypes needs no extension module and so no particular Python ABI, on one platform. The
rest of the metadata is in pyproject.toml.
"""

from setuptools import setup
from setuptools.command.bdist_wheel import bdist_wheel
from setuptools.dist import Distribution


class BinaryDistribution(Distribution):
    """A distribution that holds a native binary, though it builds no extension module.

    Saying so makes setuptools install the package as platform-specific (the wheel's root is
    platlib, not purelib), and makes bdist_wheel give the wheel a platform tag.
    """

    def has_ext_modules(self) -> bool:
        """Reports a binary in the package, which ext_modules would otherwise declare.

        Returns:
            Always True.
        """
        return True


class PlatformWheel(bdist_wheel):
    """bdist_wheel for a wheel that holds a native library but no extension module."""

    def get_tag(self) -> tuple[str, str, str]:
        """The wheel's tag: py3 and no ABI, since no extension module is built, on this platform.

        A distribution with binaries would otherwise get this Python's ABI tag (cp312-cp312),
        which would stop the wheel from installing on any other Python 3 for no reason.

        Returns:
            (python tag, ABI tag, platform tag), for example ("py3", "none", "linux_x86_64").
        """
        _, _, platform = super().get_tag()
        return "py3", "none", platform


setup(distclass=BinaryDistribution, cmdclass={"bdist_wheel": PlatformWheel})
