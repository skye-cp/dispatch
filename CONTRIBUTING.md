# Contributing

Issues and pull requests are welcome; keep changes small and focused.

## Build and test

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download). The output is a single .NET Framework 4.8 executable.

```sh
dotnet test -c Release                      # run the tests
dotnet publish src -c Release -o artifacts  # build Dispatch.exe
artifacts/Dispatch.exe --demo               # invented servers, no Steam (for screenshots)
```

Screenshots must come from `--demo`, never from real servers: they would show real players' names.

```
src/      the app (WPF): Model/ filters, query plan, host status, settings and HTTP (no UI); Steam/ lookup process and locator
tests/    unit tests (xUnit)
.config/  hook scripts and scanner settings
.github/  CI and release workflows, README images
```

## Checks

Run `pre-commit install` once per clone (it needs Python, the .NET 8 SDK and gitleaks on `PATH`). [pre-commit](https://pre-commit.com) then checks file hygiene, secrets, personal data, C# formatting, workflow security and commit messages, and runs the tests before each push. The personal-data check can also read a local denylist (see `.config/pii_wordlist.example.txt`).

## How it works

- **Server list.** Bodycam servers are Steam lobbies, and Steam returns at most 50 per request, so Dispatch asks several times: once per game mode; once per mode and map when maps are picked and that makes 30 requests or fewer; beyond that, once per map (every mode but Training) when no mode is picked, or once per mode with maps filtered on screen when modes are.
- **Isolation.** Each lookup runs in a short-lived copy of Dispatch started in lookup mode. It loads Steam's library from a private copy of the one in your Bodycam install (the game's file is only read), lists lobbies and exits. The window itself never loads Steam's library.
- **Ping.** Every lobby publishes its host's position on Steam's relay network, and Steam estimates the round trip from yours. Dispatch measures your position once, which takes a few seconds, and reuses it for two hours.
- **Age.** Steam numbers lobbies from one counter shared by every game, which rises steadily (tens of thousands a minute). Each refresh notes the newest server's number, created moments ago, so the gap to any other server's number gives its age. **NEW ROUND** means a server's map or mode changed since an earlier refresh.
- **Followed hosts.** While Dispatch is open it checks followed hosts every minute, up to ten at a time in turn, and alerts when a host it found away is hosting again.
- **Joining.** Join and invite links are Steam's own `steam://joinlobby/...` links. Steam hands the link to the game, and the game joins.

## Releases

Pushing a version tag (`v1.2.3`, matching `<Version>` in `src/Dispatch.csproj`) runs the [release workflow](.github/workflows/release.yml) on GitHub-hosted runners. It tests, builds and publishes `Dispatch.exe` with its SHA-256 checksum.
