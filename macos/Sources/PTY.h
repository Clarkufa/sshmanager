#include <sys/ioctl.h>
#include <sys/types.h>

pid_t sshm_spawn_pty(int *master, char *const argv[], char *const envp[], const char *working_directory, unsigned short cols, unsigned short rows);
int sshm_resize_pty(int master, unsigned short cols, unsigned short rows);

#include "Zip.h"
