"""Commands for opening SSH outside the app."""

import shlex
import shutil

# Terminal emulator → the option that runs a command (argv follows), in order of preference.
TERMINALS = (("x-terminal-emulator", "-e"), ("gnome-terminal", "--"), ("konsole", "-e"), ("xfce4-terminal", "-x"),
             ("mate-terminal", "--"), ("tilix", "-e"), ("kitty", None), ("alacritty", "-e"), ("foot", None),
             ("xterm", "-e"))


def terminal_command(argv, which=shutil.which):
    """argv run in the user's terminal emulator, or None if none is installed."""
    for name, option in TERMINALS:
        path = which(name)
        if not path:
            continue
        if name == "x-terminal-emulator":
            # The Debian alternative may point at a terminal that wants one shell string after -e.
            return [path, "-e", shlex.join(argv)]
        return [path, *([option] if option else []), *argv]
    return None


def ssh_command_text(alias):
    return "ssh " + alias
