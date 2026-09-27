# DiscordProxy

Self-hosted Discord webhook relay for .NET 8. Accepts the same
`POST /api/webhooks/{id}/{token}` payloads as Discord, answers `202`
immediately and delivers in the background with queueing and retries.

## Layout

```text
DiscordProxy.sln
src/DiscordProxy/      # the project (Program.cs, services, appsettings.json)
artifacts/publish/     # single-file exe built by build.bat (git-ignored)
```

## How it works

- `POST` validates the payload, stores it in SQLite and returns
  `202 { jobId, status: "queued" }`.
- A background worker sends jobs to Discord: pacing 2.2s per webhook,
  `429` waits exactly `retry_after` (plus a global pause on `global: true`),
  network errors and `5xx` retry with exponential backoff, other `4xx`
  go to the `dead` table. No message is lost on restart.
- `GET /api/webhooks/{id}/{token}` passes the check through to Discord.
- `GET /health/live`, `/health/ready`, `/stats`.

## Run

Settings live in `src/DiscordProxy/appsettings.json` (`Proxy` section).
Override with `Proxy__Port`-style environment variables, legacy `PORT` /
`DB_PATH`, or command-line args (`--Proxy:Port=7071`).

```powershell
dotnet src/DiscordProxy/bin/Release/net8.0/DiscordProxy.dll
```

Point plugins at it by swapping only the host:

```diff
- https://discord.com/api/webhooks/<id>/<token>
+ http://your-host:7070/api/webhooks/<id>/<token>
```

## Build a single exe

```powershell
.\build.bat   # -> artifacts/publish/DiscordProxy.exe
```
