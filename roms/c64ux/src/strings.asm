; Built-in command directory and ROM-resident help/initial file content.
; SPDX-License-Identifier: Apache-2.0
command_table:
    .word name_help,cmd_help
    .word name_uname,cmd_uname
    .word name_whoami,cmd_whoami
    .word name_hostname,cmd_hostname
    .word name_echo,cmd_echo
    .word name_clear,cmd_clear
    .word name_pwd,cmd_pwd
    .word name_cd,cmd_cd
    .word name_ls,cmd_ls
    .word name_cat,cmd_cat
    .word name_touch,cmd_touch
    .word name_mkdir,cmd_mkdir
    .word name_rm,cmd_rm
    .word name_rmdir,cmd_rmdir
    .word name_cp,cmd_cp
    .word name_mv,cmd_mv
    .word name_wc,cmd_wc
    .word name_hexdump,cmd_hexdump
    .word name_mem,cmd_mem
    .word name_free,cmd_mem
    .word name_uptime,cmd_uptime
    .word name_ps,cmd_ps
    .word name_sh,cmd_sh
    .word name_reboot,cmd_reboot
    .word name_dir,cmd_dir
    .word name_dcat,cmd_dcat
    .word name_import,cmd_import
    .word name_export,cmd_export
    .word name_load,cmd_load
    .word name_run,cmd_run
    .word name_play,cmd_play
    .word 0,0
name_help: .text "help\0"
name_uname: .text "uname\0"
name_whoami: .text "whoami\0"
name_hostname: .text "hostname\0"
name_echo: .text "echo\0"
name_clear: .text "clear\0"
name_pwd: .text "pwd\0"
name_cd: .text "cd\0"
name_ls: .text "ls\0"
name_cat: .text "cat\0"
name_touch: .text "touch\0"
name_mkdir: .text "mkdir\0"
name_rm: .text "rm\0"
name_rmdir: .text "rmdir\0"
name_cp: .text "cp\0"
name_mv: .text "mv\0"
name_wc: .text "wc\0"
name_hexdump: .text "hexdump\0"
name_mem: .text "mem\0"
name_free: .text "free\0"
name_uptime: .text "uptime\0"
name_ps: .text "ps\0"
name_sh: .text "sh\0"
name_reboot: .text "reboot\0"
name_dir: .text "dir\0"
name_dcat: .text "dcat\0"
name_import: .text "import\0"
name_export: .text "export\0"
name_load: .text "load\0"
name_run: .text "run\0"
name_play: .text "play\0"
banner:
    .text "C64/UX 0.2.0\nUnix-like ROM system for the 6510\n\n16 KiB ROM | 64 KiB RAM | device 8\nRAM files are volatile. Type help.\n\0"
prompt_prefix: .text "root@c64:\0"
prompt_suffix: .text "# \0"
warm_message: .text "\nShell restarted. RAM files kept.\n\0"
cancel_message: .text "^C\n\0"
version_text: .text "C64/UX 0.2.0 c64 6510\n\0"
user_text: .text "root\n\0"
host_text: .text "c64\n\0"
home_path: .text "/home/root\0"
help_text:
    .text "SYSTEM  help uname whoami hostname\n        clear mem free uptime ps reboot\nFILES   ls [path] cd [path] pwd\n        cat touch rm mkdir rmdir path\n        cp src dst | mv src dst\n        wc file | hexdump file\nSHELL   echo text | sh file\n        command > file | >> append\nDISK 8  dir | dcat name\n        import diskname ramfile\n        export ramfile diskname\n        load prgname | run\nGAMES   play name [entry] (releases RAM)\n\nQuotes group words. Up: last command.\nCtrl-C/STOP cancel. Ctrl-U erase line.\nFiles: 512 bytes, names: 15 chars.\nNo pipes, background jobs or Linux ABI.\n\0"
ps_text:
    .text "PID TTY   STATE COMMAND\n  1 tty0  RUN   sh\nSingle foreground task, no scheduler.\n\0"
mem_text:
    .text "ROM  16 KiB (shell + KERNAL)\nRAM  64 KiB physical\nAPP  $6000-$9fff: 16 KiB\nFS   32 fixed slots x 512 bytes\nNODES \0"
mem_nodes: .text "/32 allocated\nDATA  \0"
mem_bytes: .text " bytes stored\n\0"
uptime_prefix: .text "up \0"
uptime_suffix: .text " seconds (60 Hz jiffies)\n\0"
msg_usage: .text "error: invalid arguments (see help)\0"
msg_path: .text "error: path missing, invalid or FS full\0"
msg_file: .text "error: expected a regular file\0"
msg_full: .text "error: no free filesystem nodes\0"
msg_not_dir: .text "error: not a directory\0"
msg_parse: .text "error: quote mismatch or >8 arguments\0"
msg_file_full: .text "error: file exceeds 512 bytes\0"
msg_unknown: .text "error: command not found\0"
msg_busy: .text "error: directory is in use\0"
msg_not_empty: .text "error: directory not empty\0"
msg_same: .text "error: source and destination are same\0"
msg_script_long: .text "error: script line exceeds 127 bytes\0"
msg_script_nested: .text "error: nested scripts unsupported\0"
msg_script_redirect: .text "error: redirect commands inside script\0"
msg_line_long: .text "error: line too long, command discarded\0"
readme_text:
    .text "Welcome to C64/UX.\nA small Unix-like shell, no Linux ABI.\n\nTry: echo hello > note\n     cat note\n     mkdir work\n     sh hello.sh\n\nRAM files vanish on cold reset or play.\nrun: PRGs at $6000-$9fff, RTS returns.\nplay: PRGs at $0800-$cfff, takes RAM.\nA simple BASIC SYS stub can start code.\nthere is no BASIC interpreter.\n\nMount a D64 as device 8, then:\n  play romrunner\nSEQ import/export needs drive support.\n\0"
motd_text:
    .text "A tiny Unix-like home on a C64.\nNo host OS or emulator trap required.\n\0"
demo_script:
    .text "# Example script in the RAM filesystem\necho Hello from C64/UX\nuname\npwd\necho scripts work > demo.txt\ncat demo.txt\n\0"
shell_end:
