# Basis Steam Lobby

Steam lobbies for Basis. Install it and the main menu gets a **Steam Lobbies** panel: host a session
and open a Steam lobby for it, browse lobbies from friends (and public ones), invite friends, and
join over the Steam relay with no port forwarding.

It runs on the **Basis Steam Transport** package (`com.basis.transport.steam`), which provides the
`steam` network stack, Steam start-up and the Steamworks binaries, and hosts with the LiteNetLib
transport package (`com.basis.transport.litenetlib`), which Basis installs by default. Basis includes
neither Steam package;
both plug into hooks Basis already has (`BasisNetworkStackRegistry`, `BasisConnectionService`), so
removing them removes Steam completely.

| Folder | What it is |
|---|---|
| `Runtime/` | assembly `BasisSteamLobby`: Steam lobbies, the host/join session logic, world checks and the Steam Lobbies panel (`UI/`) |
| `Localization/Languages/` | the panel's strings; Basis registers a package's language files automatically |

## Installing

Install both packages in `Basis/Packages/manifest.json`. `file:` paths are relative to
`Basis/Packages`, so with both folders in `Documents` and Basis in `Documents/Github/Basis`:

```json
"com.basis.transport.steam": "file:../../../../Steam Transport Package",
"com.basis.steamlobby": "file:../../../../Steam Lobby Package"
```

Absolute paths (`"file:C:/Users/<you>/OneDrive/Documents/..."`) work too. The Steam App ID, platform
support and IL2CPP notes are in the transport package's README.

Re-hosting in the same run relies on the `BasisNetworkServerRunner.Stop` change that clears the
previous session's persistent resources and per-player saved state; without it, the second lobby you
host replays the first one's world.

## How a session works

**Hosting.** The host runs an ordinary Basis host session with two transports at once,
`litenetlib,steam` (the server's comma-separated stack list). The host's own client joins over
LiteNetLib on a free loopback port, so the host never relays its own traffic through Steam. Remote
players join through a Steam relay listen socket. Once the host is in, the package opens the Steam
lobby and writes the lobby data: host Steam ID, virtual port, Basis network version, player count,
world name and link, and a random per-session password that the server checks like any other server
password. If a world link was given, the host loads it as a persistent world, so everyone who joins
gets it.

**Joining.** Joining a lobby reads that data and connects with `BasisConnectionService.ConnectAsync`
on the `steam` stack (address `steam:<host SteamID64>:<virtual port>`), the same path the Servers
panel uses, so the loading bar, the connection watchdog and auto-reconnect all apply. A lobby from
a different Basis network version is shown but cannot be joined.

**Browsing.** Steam's lobby search never returns friends-only lobbies, so the browser also asks Steam
for the lobby each friend playing Basis is in and lists the Basis ones.

**Invites.** Accepting a Steam invite while Basis is running joins that lobby. Accepting one while
it is closed makes Steam start Basis with `+connect_lobby <id>`, and the package joins it once the
network layer is up (and stops the Servers panel from auto-connecting elsewhere first). The panel
offers both the Steam overlay invite dialog and an in-game list of online friends, which also works
in VR where the overlay is not available.

**Leaving.** Leaving the lobby, connecting to another server, quitting, or the host leaving all end
the Steam lobby membership. A dropped connection that the watchdog is retrying keeps the lobby.

**Relaunching.** A joiner that relaunches mid-session (switching graphics API restarts the app)
comes back with `--connection=steam:<host SteamID64>[:<virtual port>]#<session password>` and
reconnects over Steam. Lobby membership does not survive the restart, so once it is back in, the
package rejoins the host's lobby when the host is a Steam friend. A relaunching host restarts as a
plain LiteNetLib host on the same port; its Steam lobby does not come back.

Steam starts the first time the Steam Lobbies panel is opened (or when Steam launched the game), so
players who never use it never appear in Steam as playing Basis Labs (app 3157090, the App ID both
Steam packages use by default).

## Testing it

Steam networking needs two Steam accounts on two machines (or two Steam clients):

1. Host: open Steam Lobbies, pick who can join, optionally paste a world link and password, Host.
2. Second account: Refresh, select the lobby, Join; or accept an invite sent from the host's panel.
3. Check avatars, voice, props and the world appear on both sides; leave and host again in the same
   run (the second lobby must not show the first world); quit the host and check the joiner gets
   the "server closed" message and leaves the lobby.

## Credits

The Steam lobbies and the lobby panel were first written by
[saint4626 (Momonth)](https://github.com/saint4626) in BasisVR/Basis pull requests #818 and #827.
This package reworks that work as a standalone package on top of the current Basis transport hooks.
