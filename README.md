<div align="center">

<img src=".github/assets/logo.png" width="88" alt="Dispatch logo">

# Dispatch

**Find a Bodycam server, join it, or send a friend the link.**

[![Latest release](https://img.shields.io/github/v/release/skye-cp/dispatch?label=release&color=d8433b)](https://github.com/skye-cp/dispatch/releases/latest) [![Tests](https://img.shields.io/github/actions/workflow/status/skye-cp/dispatch/ci.yml?branch=main&label=tests)](https://github.com/skye-cp/dispatch/actions/workflows/ci.yml) [![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-555)](#requirements) [![License: MIT](https://img.shields.io/badge/license-MIT-555)](LICENSE)

<br>

[![Download for Windows](https://img.shields.io/badge/Download%20for%20Windows-d8433b?style=for-the-badge)](https://github.com/skye-cp/dispatch/releases/latest/download/Dispatch.exe)

<sub>One portable file, under 150 KB · <a href="https://github.com/skye-cp/dispatch/releases">All releases</a></sub>

<br>
<br>

<img src=".github/assets/screenshot.png" width="860" alt="Dispatch listing Bodycam servers sorted by ping, with mode, map, region, ping and bot filters">

</div>

<br>

Bodycam's party system is offline, so friends can't queue together, and the in-game server list has no search, filters or ping. Dispatch reads the same public server list from Steam, lets you sort and filter it, and puts you or your friends into a chosen server through Steam's own join link.

## Features

- **Every server at a glance.** About 300 at a time, against the 50 a single Steam search returns.
- **Ping for each server.** Lowest first, estimated through Steam's relay network.
- **Filters that combine.** Game mode, map, region, ping and bots, with several values of each, remembered between sessions.
- **Click to filter.** Click a mode, map, bot setting, region or ping in the list to filter by it; click again to undo.
- **Friends first.** Servers hosted by your Steam friends are marked and pinned to the top.
- **Follow hosts.** Star a host to pin their server and get an alert when it appears.
- **Host search.** Type a host's name to find the server a friend is playing on.
- **Join, invite, rejoin.** One click to join; invite links put friends in the same server; **Recent** rejoins a host after a disconnect.
- **Auto-refresh.** Optional, every minute while Dispatch is open, with new servers marked. Or pull past the top or bottom of the list to refresh.
- **Smooth and quiet.** The list updates in place with gentle animations; a few short sounds mark joins, copied invites and alerts, with a mute button in the title bar.
- **Server age.** How long each server has been up, to tell fresh servers still filling up from established ones.
- **New rounds.** Servers whose map just changed are marked, the best moment to join.
- **Players online.** How many people are playing Bodycam right now, from Steam.
- **Light and fast.** One small executable that opens in half a second.

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

To narrow the list, use the filter bar above it (each button opens a small menu and shows what is selected) or click a value in the list itself, such as a mode, map or region. **Reset** clears everything.

### Play with a friend

- **Bring them to your server.** Select it, press **Copy invite link** and send the link. Your friend opens it with Bodycam running, either with **Join pasted link** in Dispatch or by pasting it into <kbd>Win</kbd>+<kbd>R</kbd>.
- **Go to theirs.** If they host it, it is already at the top, marked **FRIEND**. Otherwise type their server's host name, the name shown in the game's server list, and press <kbd>Enter</kbd>.
- **Wait for them.** Click the star next to a host's name. While Dispatch is open, it checks every minute and alerts you (a sound and a flashing taskbar button) when that host's server appears.

### Shortcuts

| Key | Action |
| --- | --- |
| <kbd>Enter</kbd> or double-click | Join the selected server |
| <kbd>Ctrl</kbd>+<kbd>C</kbd> | Copy its invite link |
| <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>V</kbd> | Join the link on the clipboard |
| <kbd>Ctrl</kbd>+<kbd>F</kbd> | Search host names; <kbd>Enter</kbd> searches every server |
| <kbd>F5</kbd>, or scroll on past the top or bottom | Refresh |
| <kbd>Esc</kbd> | Clear the search |

## FAQ

<details>
<summary><b>Is it safe to use with Easy Anti-Cheat?</b></summary>
<br>

Dispatch never reads or writes the game's memory, files or network traffic, injects nothing and sends no input to the game. It talks only to Steam, the way Steam's own invites do.

</details>

<details>
<summary><b>Is it allowed?</b></summary>
<br>

Dispatch is unofficial and not endorsed by Reissad Studio. It gives no gameplay advantage: it shows the same public server list the game uses. However, Bodycam's terms prohibit "unauthorized programs" in connection with the game's online features, and Steam's terms restrict unauthorized third-party software, without defining either. Enforcement is at their discretion, so **use Dispatch at your own risk**. If Reissad Studio or Valve asks for it to be taken down, it will be.

</details>

<details>
<summary><b>Why are there no player counts?</b></summary>
<br>

Steam doesn't publish them for Bodycam servers: players connect to a host without joining its Steam lobby, so only the host is listed. <i>Slots</i> is the server's size, and the status line shows how many people are playing Bodycam in total. If a server turns out to be full, the game says the connection failed and returns to the menu.

</details>

<details>
<summary><b>How is a server's age worked out?</b></summary>
<br>

Steam numbers lobbies from one counter shared by every game, which rises steadily (tens of thousands a minute). Each refresh notes the newest server's number, which was created moments ago, so the gap to any other server's number gives its age. Expect accuracy within a few minutes. A **NEW ROUND** mark means the server's map or mode changed since an earlier refresh, so turn on **AUTO** to catch them.

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

Each lookup briefly runs as Bodycam's Steam app (about a second) to read the game's server list, which Steam counts as a moment of Bodycam playtime. Auto-refresh and following hosts repeat that once a minute while Dispatch is open.

</details>

## How it works

- **Server list.** Bodycam servers are Steam lobbies. Dispatch asks Steam for the public lobby list once per game mode, and per map when maps are picked, because Steam returns at most 50 lobbies per request.
- **Ping.** Every lobby publishes its host's position on Steam's relay network, and Steam estimates the round trip from yours.
- **Isolation.** Each lookup runs in a short-lived copy of Dispatch started in lookup mode. It loads Steam's library from your own Bodycam install (a private copy, so the game's file is only read), lists lobbies and exits. The window itself never talks to Steam.
- **Joining.** Join and invite links are Steam's own `steam://joinlobby/...` links. Steam hands the link to the game, and the game joins.

## Privacy

Dispatch reads only public lobby data: host name, mode, map, bots, size and relay ping data. To mark friends' servers it reads your Steam friends list from the Steam client; that stays in memory and is never saved or sent. It saves your filters, sort order, followed hosts, recent servers, window position and measured relay position as you change them, in `%APPDATA%\Dispatch`, and its copy of Steam's library in `%LOCALAPPDATA%\Dispatch`.

It also asks Steam's public Web API how many people are playing Bodycam, at most once a minute. Besides these Steam requests, Dispatch makes one other network request: at most every 12 hours it asks GitHub for the latest release, to show an *Update available* link. The request carries no personal data. To turn it off, add `updateCheck=0` to `%APPDATA%\Dispatch\settings.ini`.

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
- Privacy: apart from the Steam requests it exists to make (listing public lobbies, measuring relay ping and reading the total player count) and an optional check for new releases on GitHub (see [Privacy](#privacy)), Dispatch will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

## License

[MIT](LICENSE). Dispatch is a fan-made tool, not affiliated with or endorsed by Reissad Studio or Valve. Bodycam and Steam are trademarks of their owners.
