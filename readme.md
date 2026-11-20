# DownswitchRenegotiator
A Windows tray utility for when your router switches from gigabit on the WAN port to 100 Mbps for no clear reason

## Note: This project was vibecoded.

## Why?
Because my router keeps switching from a gigabit connection to 100 Mbps, and I didn't really want to monitor for when that happens, and then physically reconnect the cable.

## Requirements
- Windows with the built-in OpenSSH Client (`ssh.exe` / `ssh-keygen.exe`) and .NET Framework 4.x (`csc.exe`).
- A router with SSH access that supports `swconfig` and `mii_mgr` (tested on Asus AX1800U / RT-AX53U stock firmware).

## How to use?
1. Clone the repository, then run `.\generate-key.ps1` and `.\build.ps1`.
2. Launch `DownswitchRenegotiator.exe` once—it will copy `default_config.ini` to `config.ini`, show a message, and exit. Open `config.ini` and fill in your router's IP, SSH port, login (and `wan_port` if yours isn't `0`; ports `0`–`31` are supported).
3. Copy the public key printed by `generate-key.ps1` (also saved in `ssh\id_ed25519.pub`) and paste it into your router's SSH Authorized Keys setting. For my router (Asus AX1800U/RT-AX53U on default firmware) that was `Administration -> System -> SSH -> Keys`. Don't forget to enable SSH and pick a port while you're there.
4. Launch `DownswitchRenegotiator.exe` again. It runs in the Windows system tray, automatically checks whether the WAN link dropped below gigabit, and tries to renegotiate it. Right-click the tray icon to check/renegotiate manually, change the check interval, configure notifications, or enable `Start with Windows`.

On the first connection, the router's SSH host key is automatically saved to `ssh\known_hosts` and verified on future runs (if you reset or replace your router and its host key changes, delete `ssh\known_hosts` so it can be re-pinned).

## What if I want to change it to work on my router?
Idk, fork it? I am not a networks engineer, so I don't fully understand what the program does, and how to make it adaptive to your router.
