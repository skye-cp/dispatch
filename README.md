<div align="center">

<img src=".github/assets/logo.png" width="88" alt="Dispatch logo">

# Dispatch

**Find a Bodycam server, join it, or send a friend the link.**

[![Latest release](https://img.shields.io/github/v/release/skye-cp/dispatch?label=release&color=d8433b)](https://github.com/skye-cp/dispatch/releases/latest) [![Tests](https://img.shields.io/github/actions/workflow/status/skye-cp/dispatch/ci.yml?branch=main&label=tests)](https://github.com/skye-cp/dispatch/actions/workflows/ci.yml) [![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-555)](#requirements) [![License: MIT](https://img.shields.io/badge/license-MIT-555)](LICENSE)

<br>

[![Download for Windows](https://img.shields.io/badge/Download%20for%20Windows-d8433b?style=for-the-badge)](https://github.com/skye-cp/dispatch/releases/latest/download/Dispatch.exe)

<sub>One portable file, about 115 KB · <a href="https://github.com/skye-cp/dispatch/releases">All releases</a></sub>

<br>
<br>

<img src=".github/assets/screenshot.png" width="860" alt="Dispatch listing Bodycam servers sorted by ping, with mode, map, region, ping and bot filters">

</div>

<br>

Bodycam's party system is offline, so friends can't queue together, and the in-game server list has no search, filters or ping. Dispatch reads the same public server list from Steam, lets you sort and filter it, and puts you or your friends into a chosen server through Steam's own join link.

## Features

- **Every server at a glance.** About 300 at a time, against the 50 a single Steam search returns.
- **Ping for each server.** Lowest first, estimated through Steam's relay network.
- **Filters that combine.** Game mode, map, region, ping and bots, with several values of each.
- **Host search.** Type a host's name to find the server a friend is playing on.
- **Join and invite.** One click to join; invite links put friends in the same server.
- **Light and fast.** One small executable that opens in half a second and remembers your filters.

## Requirements

- Windows 10 or 11 (.NET Framework 4.8, which ships with Windows)
- Steam, running and signed in
- Bodycam, installed

## Quick start

1. [Download `Dispatch.exe`](https://github.com/skye-cp/dispatch/releases/latest/download/Dispatch.exe) and run it. Nothing to install.
2. Start Bodycam and wait at the main menu.
3. Pick a server and press **Join**.

> [!NOTE]
> Releases are not code-signed yet, so Windows may warn about an unknown publisher on the first run. Choose **More info**, then **Run anyway**. See the [code signing policy](#code-signing-policy).

### Play with a friend

- **Bring them to your server.** Select it, press **Copy invite link** and send the link. Your friend opens it with Bodycam running, either with **Join pasted link** in Dispatch or by pasting it into <kbd>Win</kbd>+<kbd>R</kbd>.
- **Go to theirs.** Type their server's host name, the name shown in the game's server list, and press <kbd>Enter</kbd>.

### Shortcuts

| Key | Action |
| --- | --- |
| <kbd>Enter</kbd> or double-click | Join the selected server |
| <kbd>Ctrl</kbd>+<kbd>C</kbd> | Copy its invite link |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>V</kbd> | Join the link on the clipboard |
| <kbd>Ctrl</kbd>+<kbd>F</kbd> | Search host names; <kbd>Enter</kbd> searches every server |
| <kbd>F5</kbd> | Refresh |
| <kbd>Esc</kbd> | Clear the search |

## FAQ

<details>
<summary><b>Is it safe to use with Easy Anti-Cheat?</b></summary>
<br>

Dispatch never reads or writes the game's memory, files or network traffic, injects nothing and sends no input to the game. It talks only to Steam, the way Steam's own invites do. It is still an unofficial tool, so use it at your own risk.

</details>

<details>
<summary><b>Why are there no player counts?</b></summary>
<br>

Steam doesn't publish them for Bodycam servers. <i>Slots</i> is the server's size. If a server turns out to be full, the game says the connection failed and returns to the menu.

</details>

<details>
<summary><b>How accurate is the ping?</b></summary>
<br>

It is Steam's estimate of the round trip through its relay network, from your measured position to the host's. It's close, but the game may route differently. Dispatch measures your position once, which takes a few seconds, and reuses it for two hours.

</details>

<details>
<summary><b>A join didn't work.</b></summary>
<br>

Join from Bodycam's main menu: opening a link while the game is closed has failed in testing, so Dispatch asks before doing that. Wingman matches can't be joined once they have started.

</details>

<details>
<summary><b>Why does Steam show me in Bodycam for a moment?</b></summary>
<br>

Each lookup briefly runs as Bodycam's Steam app (about a second) to read the game's server list.

</details>

## How it works

- **Server list.** Bodycam servers are Steam lobbies. Dispatch asks Steam for the public lobby list once per game mode, and per map when maps are picked, because Steam returns at most 50 lobbies per request.
- **Ping.** Every lobby publishes its host's position on Steam's relay network, and Steam estimates the round trip from yours.
- **Isolation.** Each lookup runs in a short-lived copy of Dispatch started in lookup mode. It loads Steam's library from your own Bodycam install (a private copy, so the game's file is only read), lists lobbies and exits. The window itself never talks to Steam.
- **Joining.** Join and invite links are Steam's own `steam://joinlobby/...` links. Steam hands the link to the game, and the game joins.

## Privacy

Dispatch reads only public lobby data: host name, mode, map, bots, size and relay ping data. It keeps your filters, window position and measured relay position in `%APPDATA%\Dispatch`, and its copy of Steam's library in `%LOCALAPPDATA%\Dispatch`. It sends nothing anywhere beyond the Steam requests above.

## Development

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download). The output is a single .NET Framework 4.8 executable.

```sh
dotnet test -c Release                      # run the tests
dotnet publish src -c Release -o artifacts  # build Dispatch.exe
artifacts/Dispatch.exe --demo               # invented servers, no Steam (for screenshots)
```

```
src/      the app (WPF): Model/ filters and caching, Steam/ lookup process and locator
tests/    unit tests (xUnit)
.config/  hook scripts and scanner settings
.github/  CI and release workflows, README images
```

**Contributing.** Issues and pull requests are welcome; keep changes small and focused. Run `pre-commit install` once per clone, and [pre-commit](https://pre-commit.com) then checks file hygiene, secrets, personal data, C# formatting, workflow security and commit messages, and runs the tests before each push. The personal-data check can also read a local denylist (see `.config/pii_wordlist.example.txt`).

**Releases.** Pushing a version tag (`v1.2.3`, matching `<Version>` in `src/Dispatch.csproj`) runs the [release workflow](.github/workflows/release.yml) on GitHub-hosted runners. It tests, builds, signs and publishes `Dispatch.exe` with its SHA-256 checksum.

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org). The application is pending; until it is approved, releases are unsigned and say so in their notes.

- Only release builds from this repository's release workflow are signed, each after manual approval.
- Committers and reviewers: [skye-cp](https://github.com/skye-cp). Approvers: [skye-cp](https://github.com/skye-cp).
- Privacy: apart from the Steam requests it exists to make (listing public lobbies and measuring relay ping), Dispatch will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

## License

[MIT](LICENSE). Dispatch is a fan-made tool, not affiliated with or endorsed by Reissad Studio or Valve. Bodycam and Steam are trademarks of their owners.
