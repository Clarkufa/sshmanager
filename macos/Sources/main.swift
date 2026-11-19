import AppKit

if AskPass.requested { AskPass.run() }
signal(SIGPIPE, SIG_IGN)
SSHManagerApp.main()
