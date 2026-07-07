# Z+ — C# Client-Server Video Conferencing

A Zoom-style video conferencing platform for Windows: WPF desktop client + ASP.NET Core backend
with SignalR signaling and WebRTC (SIPSorcery) peer-to-peer media.

This is **Phase 1 (MVP)** of the larger platform roadmap: accounts, instant/scheduled meetings,
join by ID + password, group audio/video, in-meeting chat (public & private), participant roster,
and host controls.

## Solution layout

| Project | Purpose |
|---|---|
| `src/ZPlus.Shared` | DTOs and hub method/event contracts shared by client and server |
| `src/ZPlus.Server` | ASP.NET Core Web API (JWT auth, meetings, admin) + SignalR `MeetingHub` (roster, chat, WebRTC signaling relay, host controls) + EF Core/SQLite persistence |
| `src/ZPlus.Client` | WPF (.NET 10) MVVM client with SIPSorcery WebRTC mesh media engine |
| `src/ZPlus.Admin` | WPF admin console: user management, roles/rights, server settings, active-meeting control |
| `src/ZPlus.ClientGui` | Cross-platform Avalonia meeting client (`zplus-client`) — the Linux GUI: meetings, roster, E2EE chat |
| `src/ZPlus.AdminGui` | Cross-platform Avalonia admin console (`zplus-admin`) — the Linux GUI, same features as the WPF admin |

## Building single-file executables

Each application publishes to **one self-contained binary** (all DLLs and the .NET runtime
embedded — nothing to install on the target machine).

**Windows** (net8.0 / net10.0-windows GUI apps):

```
dotnet publish src/ZPlus.Server -c Release -f net8.0 -o publish/server
dotnet publish src/ZPlus.Client -c Release -o publish/client
dotnet publish src/ZPlus.Admin  -c Release -o publish/admin
```

Copy `Z+ Server.exe`, `Z+ Client.exe`, and `Z+ Admin.exe` wherever you need them.

**Linux** (net10.0, into `publish/linux`) — use the `Makefile` at the repository root:

```
make            # builds zplus-server, zplus-client and zplus-admin
make server     # or build one target: server / client / admin
make clean
```

Requires GNU make and the .NET 10 SDK. The Linux binaries target **net10.0** (Windows
builds stay on net8.0); the cross-platform projects multi-target `net8.0;net10.0`.
The RuntimeIdentifier is **auto-detected from the build machine** (`x86_64` → linux-x64,
`aarch64` → linux-arm64, `armv7l/armv6l` → linux-arm, with `linux-musl-*` on Alpine).
Cross-build by overriding it, e.g. `make RID=linux-arm64`; `make DOTNET=dotnet.exe`
runs the Makefile from WSL using the Windows .NET SDK.

## Linux

`publish/linux/` contains three self-contained x64 binaries — no .NET install required
(`chmod +x` them after copying):

- **`zplus-server`** — the full Z+ server, identical feature set to Windows. Creates
  `zplus.db` and `zplus.key` next to itself, binds `0.0.0.0:5199`. All environment
  overrides (`ZPLUS_DB`, `ZPLUS_KEY`, `ZPLUS_ADMIN_EMAIL`, `ZPLUS_ADMIN_PASSWORD`) work
  the same. Windows and Linux clients connect to it interchangeably.
- **`zplus-client`** — the Linux **desktop meeting client** (Avalonia, same dark Z+ look
  as Windows): sign in/register, start/join/schedule meetings with email invitations,
  live roster with host controls, end-to-end encrypted public/private chat.
  **No audio/video yet** — the media capture stack (Windows Media Foundation) is
  Windows-only; Linux participants join with roster + chat.
- **`zplus-admin`** — the Linux **desktop admin console** (Avalonia), same features as
  the Windows admin app: users/roles, all server settings including SMTP, active
  meetings with force-end.

The GUI apps share their view-models with the Windows WPF apps and also run on Windows.

## Running it

1. **Server** — run `Z+ Server.exe` (or `dotnet run --project src/ZPlus.Server`).
   On first run, next to the executable, it creates:
   - `zplus.db` — SQLite database holding **all users and all server configuration**
   - `zplus.key` — the 64-byte AES/HMAC master key protecting secrets inside the database

   It binds to `http://0.0.0.0:5199` by default so LAN clients can connect (Windows Firewall
   will prompt to allow inbound TCP 5199), and seeds the first super admin
   (`admin@zplus.local` / `ChangeMe123!` — **change this immediately**).

   Optional environment overrides: `ZPLUS_DB` (db path), `ZPLUS_KEY` (key file path),
   `ZPLUS_ADMIN_EMAIL` / `ZPLUS_ADMIN_PASSWORD` (first-run seed account).
2. **Client** — run `Z+ Client.exe` on each participant's machine; point the *Server*
   field at `http://<server-ip>:5199`, create an account, then start or join a meeting.
3. **Admin** — run `Z+ Admin.exe` and sign in with an Admin/SuperAdmin account:
   - **Users** — create accounts, edit display names, assign roles (`User`, `Admin`,
     `SuperAdmin`), enable/disable accounts, reset passwords. Disabled accounts cannot
     sign in or join meetings. Only super admins can create/modify super admin accounts;
     nobody can demote or disable themselves.
   - **Server settings** — allow/deny self-registration, require passwords on all meetings,
     cap participants per meeting, change the server listen URL (restart required), and
     configure email invitations: SMTP host/port/from/credentials plus the Public URL used
     in invite links. All stored in the database and enforced live.
   - **Active meetings** — see currently running meetings with participant counts and
     force-end any meeting (participants are notified instantly).

## Security model

- **End-to-end encryption.** Audio/video flows peer-to-peer over DTLS-SRTP and never
  touches the server. Chat is AES-256-GCM encrypted with a per-meeting key that
  participants exchange directly via ECDH (P-256) — the server relays and stores only
  `ZE1$…` ciphertext, so neither admins nor database access can read messages.
- **Passwords are hashed, then encrypted (AES + HMAC).** Every password (user accounts and
  meeting passwords) is first one-way hashed with PBKDF2 — so the original password can never
  be recovered — and the hash is then wrapped in an AES-256-CBC + HMAC-SHA256
  encrypt-then-MAC envelope (`ZP1$…` values in the database). The HMAC rejects any tampered
  or foreign ciphertext in constant time.
- **Key separation.** The AES/HMAC master key lives in `zplus.key`, *outside* the database:
  a stolen `zplus.db` exposes no usable credential material without the key file. Back up
  the key file securely; if it is lost, stored secrets cannot be decrypted (users would
  need password resets).
- **Configuration lives in SQLite.** There is no `appsettings.json`. Server behavior
  settings, the listen URL, and the JWT signing key (auto-generated, encrypted at rest)
  are all rows in the `ServerSettings` table, manageable through the admin app.
- **JWTs** (12 h expiry) carry the user's role; SignalR connections authenticate via the
  `access_token` query parameter. Admin endpoints require the Admin/SuperAdmin role;
  disabled accounts are re-checked at meeting join, not just at login.

## Architecture notes

- **Signaling**: SignalR hub relays SDP offers/answers and ICE candidates between peers;
  the hub validates that both peers are in the same meeting.
- **Media**: full-mesh WebRTC — each client holds one `RTCPeerConnection` per remote
  participant, sharing a single microphone and camera source (VP8 video, one encode fan-out).
  The joining client always initiates offers, which avoids negotiation glare.
- **Host controls**: mute all, ask-to-unmute, remove participant, transfer host, end for all.
  Host role auto-promotes to the longest-connected participant if the host drops.
- **Persistence**: users, meetings, attendance records, chat history, and configuration in
  SQLite (swap the EF Core provider for SQL Server/PostgreSQL in production). Live roster
  state is in-memory (`MeetingStateStore`).

## Roadmap (per project spec)

- **Phase 2**: screen sharing, waiting room, file sharing, recording, reactions
- **Phase 3**: breakout rooms, whiteboard, polls, captions/transcription, admin dashboard
- **Phase 4**: SSO, MFA, audit logs, compliance, large meetings (SFU media server)
- **Phase 5**: AI meeting summaries, action items, translation, smart search

## Production hardening TODOs

- Serve over HTTPS/WSS with real certificates.
- Add TURN servers for NAT traversal beyond STUN (`stun.l.google.com` is used for dev).
- Move from mesh to an SFU for meetings beyond ~4-6 participants.
- Switch `EnsureCreated` to EF Core migrations before real data matters.
