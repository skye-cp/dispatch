# Dispatch

Find a Bodycam server, join it, or send a friend a link that puts them in the same server.

Bodycam's party system is offline, so friends can't queue together, and the in-game server list has no search or filters. Dispatch reads the same public server list from Steam, lets you filter and search it, and joins through Steam's own join link.

## Use

Requires Windows 10 or 11, Steam running, and Bodycam installed. Nothing to install: run `Dispatch.exe`.

1. Start Bodycam and wait at the main menu.
2. Pick a server and press **Join**. The game connects to it.
3. To bring a friend, press **Copy invite link** and send it. Your friend opens it with Bodycam running, either with **Join pasted link** in Dispatch or by pasting it into Win+R.

To join a friend who is already playing, type the host name of their server (the name shown in the game's server list) and press Enter.

| Key | Action |
| --- | --- |
| Enter, double-click | Join the selected server |
| Ctrl+C | Copy its invite link |
| Ctrl+Shift+V | Join the link on the clipboard |
| Ctrl+F | Search host names; Enter searches every server |
| F5 | Refresh from Steam |
| Esc | Clear the search |

Filters combine: several values of one kind match any of them (Zombie *or* Versus); different kinds must all match.

## How it works

- **Server list.** Bodycam servers are Steam lobbies. Dispatch asks Steam for the public lobby list, once per game mode (and per map when maps are picked), because Steam returns at most 50 lobbies per request.
- **Isolation.** Each lookup runs in a short-lived copy of Dispatch started in lookup mode. It loads Steam's library from your own Bodycam install (a private copy, so the game's file is only read), lists lobbies and exits. The window itself never talks to Steam.
- **Joining.** Join and invite links are Steam's own `steam://joinlobby/...` links, the same mechanism as Steam invites. Steam hands the link to the game, and the game joins.

Dispatch never reads or writes the game's memory, files or network traffic, injects nothing and sends no input to the game. It is still an unofficial tool: use it at your own risk.

## Limits

- **No player counts.** Steam doesn't publish them; *Slots* is the server's size. A full server fails to join and the game returns to the menu.
- **Join from the menu.** Opening a link while Bodycam is closed has failed in testing, so Dispatch asks before launching the game that way.
- **Steam status.** During a lookup (about a second) Steam shows you as playing Bodycam.

## Privacy

Dispatch reads only public lobby data: host name, mode, map, bots, size and relay region. It keeps filter and window settings in `%APPDATA%\Dispatch` and its copy of Steam's library in `%LOCALAPPDATA%\Dispatch`, and sends nothing anywhere.

## Build

Needs the .NET 8 SDK. The output is a single .NET Framework 4.8 executable, a runtime that ships with Windows.

```
dotnet test -c Release
dotnet publish src -c Release -o artifacts
```

## Develop

```
src/      the app (WPF): Model/ filters and caching, Steam/ lookup process and locator
tests/    unit tests (xUnit)
.config/  hook scripts and scanner settings
```

Quality gates run through [pre-commit](https://pre-commit.com): `pre-commit install` once per clone. They check file hygiene, secrets (gitleaks, detect-secrets), personal data, C# formatting and Conventional Commits, and run the tests before each push. `main` takes merges only. The personal-data check also reads an optional local denylist; see `.config/pii_wordlist.example.txt`.

## Disclaimer

Dispatch is a fan-made tool, not affiliated with or endorsed by Reissad Studio or Valve. Bodycam and Steam are trademarks of their owners.
