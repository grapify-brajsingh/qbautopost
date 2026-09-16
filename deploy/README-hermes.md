# Hermes on the QuickBooks server

QbAutopost reads statements and invoices through Hermes, an OpenAI-compatible API (`/v1/chat/completions`)
running in Docker on the same server (spec §4). This page sets it up. The operator guide is `docs/runbook.md`.

| Rule | How |
|---|---|
| Hermes is reachable from this server only | `docker-compose.yml` publishes `127.0.0.1:8642` only. Never change it to `8642:…` or `0.0.0.0:…` |
| A key is required | `API_SERVER_KEY` must be set; compose refuses to start without it |
| The cloud model key stays in the container | `provider.env` goes into the container only (spec Q5); QbAutopost never sees it |
| No secrets in git | `.env` and `provider.env` are git-ignored; the `*.example` files hold placeholders only |

## 1. Install Docker

### Windows Server 2022 (and later): WSL 2 + Docker Engine

Docker Desktop is not supported on Windows Server, so Docker Engine runs inside a WSL 2 Linux distribution.

1. In an elevated PowerShell:
   ```powershell
   wsl --install -d Ubuntu      # reboot when asked, then open "Ubuntu" once and create the Linux user
   wsl --set-default-version 2
   wsl -l -v                    # Ubuntu must show VERSION 2
   ```
2. Inside Ubuntu (`wsl -d Ubuntu`), install Docker Engine from Docker's apt repository
   (https://docs.docker.com/engine/install/ubuntu/), then:
   ```bash
   sudo usermod -aG docker "$USER"     # log out of WSL and back in
   docker run --rm hello-world
   ```
3. Let Docker start with the distribution. Put this in `/etc/wsl.conf` inside Ubuntu, then `wsl --shutdown` in PowerShell:
   ```ini
   [boot]
   systemd=true
   ```
   and `sudo systemctl enable --now docker`.
4. Ports published on `127.0.0.1` inside WSL 2 are forwarded to the Windows loopback (`localhostForwarding`, on by default).
   Check from Windows after step 3 of section 3: `Test-NetConnection 127.0.0.1 -Port 8642`.

Run the compose commands below **inside Ubuntu** (`wsl -d Ubuntu`), from the repository copy on the server, e.g.
`cd /mnt/c/qb-autopost/deploy/hermes`. `deploy/start-all.ps1` does this for you (`-DockerMode Wsl`, the default).

### Windows 10/11: Docker Desktop

1. Install Docker Desktop (WSL 2 backend) and sign in to the auto-logon account.
2. Settings → General → "Start Docker Desktop when you sign in to your computer".
3. Run the compose commands below in PowerShell from `deploy\hermes`. Use `deploy/start-all.ps1 -DockerMode Desktop`.

### Memory cap (`.wslconfig`)

WSL 2 may take up to half of the server's memory, which QuickBooks needs too. Cap it in
`%UserProfile%\.wslconfig` of the account that runs WSL, then `wsl --shutdown`:

```ini
[wsl2]
memory=6GB
processors=2
```

The container itself is capped by `HERMES_MEMORY_LIMIT` (default `4g`); keep it below the `.wslconfig` value.

## 2. Configure

```powershell
cd deploy\hermes
Copy-Item .env.example .env
Copy-Item provider.env.example provider.env
notepad .env
notepad provider.env
```

| Variable (`.env`) | Meaning |
|---|---|
| `HERMES_IMAGE` | The Hermes Agent image with an explicit tag or digest. **No default**: the spec does not name the image (tracker Q-42) |
| `HERMES_CONTAINER_PORT` | Port of the OpenAI-compatible API inside the container (`8642` unless the image documents another). The host side is always `127.0.0.1:8642` |
| `HERMES_DATA_PATH` | Folder inside the container where Hermes keeps its configuration (stored in the `hermes-data` volume) |
| `API_SERVER_KEY` | Long random value. Required |
| `HERMES_MEMORY_LIMIT` | Optional container memory cap, default `4g` |

`provider.env` holds the cloud model provider's key under the variable name that provider and image expect
(for example the provider's `*_API_KEY`). The container also receives `API_SERVER_ENABLED=true`,
`API_SERVER_HOST=0.0.0.0` (inside the container only) and `API_SERVER_PORT`. If your image uses other names for
these settings, put the right ones in `provider.env` and record it in the tracker (Q-42).

Give QbAutopost the same key, in the auto-logon account's environment (not in a file):

```powershell
[Environment]::SetEnvironmentVariable('QBAUTOPOST__Hermes__ApiKey', '<API_SERVER_KEY>', 'User')
```

`Hermes:BaseUrl` stays `http://127.0.0.1:8642`; `Hermes:Model` is the model name Hermes expects (`default` unless told otherwise).

## 3. Start and check

```powershell
docker compose config --quiet     # validates .env; prints nothing when fine (plain "config" prints the secrets)
docker compose up -d
docker compose ps                 # STATUS "Up"
docker compose logs --tail 50 hermes
docker port qbautopost-hermes     # must show 127.0.0.1:8642 only
```

Then, with QbAutopost running:

```powershell
Invoke-RestMethod http://127.0.0.1:5080/health/hermes   # { ok: true, model, latencyMs }
```

`/health/hermes` sends one tiny completion through Hermes, so it also proves the provider key works.
A 503 with `401`/`403` means `API_SERVER_KEY` and `QBAUTOPOST__Hermes__ApiKey` differ; a timeout or
"connection refused" means the container is not up or the port is not forwarded.

## 4. Operate

| Task | Command (in `deploy/hermes`) |
|---|---|
| Stop | `docker compose down` (the volume is kept) |
| Update the image | change `HERMES_IMAGE`, then `docker compose pull; docker compose up -d` |
| Rotate the key | change `API_SERVER_KEY` and `QBAUTOPOST__Hermes__ApiKey`, `docker compose up -d`, restart QbAutopost |
| Logs | `docker compose logs hermes` (10 MB × 3 kept) |

Never run `docker compose config` output into a ticket or chat: it contains the keys.
