#include "PTY.h"
#include <errno.h>
#include <fcntl.h>
#include <signal.h>
#include <unistd.h>
#include <util.h>

pid_t sshm_spawn_pty(int *master, char *const argv[], char *const envp[], const char *working_directory, unsigned short cols, unsigned short rows)
{
    struct winsize size = { .ws_col = cols, .ws_row = rows };
    pid_t child = forkpty(master, NULL, NULL, &size);
    if (child == 0) {
        // Only async-signal-safe operations between fork and exec.
        if (chdir(working_directory) == 0) execve(argv[0], argv, envp);
        static const char message[] = "Unable to start OpenSSH.\r\n";
        (void)write(STDERR_FILENO, message, sizeof(message) - 1);
        _exit(127);
    }
    if (child > 0 &&
        (fcntl(*master, F_SETFD, FD_CLOEXEC) == -1 ||
         fcntl(*master, F_SETFL, O_NONBLOCK) == -1)) {
        int error = errno;
        close(*master);
        kill(child, SIGHUP);
        errno = error;
        // The caller still owns the child and reaps it.
        *master = -1;
    }
    return child;
}

int sshm_resize_pty(int master, unsigned short cols, unsigned short rows)
{
    struct winsize size = { .ws_col = cols, .ws_row = rows };
    return ioctl(master, TIOCSWINSZ, &size);
}
