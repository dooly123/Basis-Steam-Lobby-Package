using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Common;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.NetworkedAvatar;
using Steamworks;
using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Network.Steam
{
    public sealed class BasisSteamHostRequest
    {
        public string LobbyName = string.Empty, WorldUrl = string.Empty, WorldPassword = string.Empty;
        public BasisSteamLobbyVisibility Visibility = BasisSteamLobbyVisibility.FriendsOnly;
        public int MaxMembers = BasisSteamSession.DefaultMaxMembers;
    }
    [AutoStaticsCleanup]
    public static partial class BasisSteamSession
    {
        public const int DefaultMaxMembers = 32;
        public const string EntrySourceId = "steam";
        public static string LastError = string.Empty;
        public static event Action<string> StatusChanged;
        public static event Action<string> Failed;
        private static int sessionCounter, activeSession, connectedSession;
        private static PendingHost pendingHost;
        private static ulong launchLobby;
        private static bool busy;
        private sealed class PendingHost
        {
            public int Session, MaxMembers;
            public string Name, SessionKey;
            public BasisSteamLobbyVisibility Visibility;
            public BasisSteamWorldCheck World;
        }
        public static string HostStacks => BasisNetworkStackRegistry.LiteNetLibId + "," + BasisSteamTransport.StackId;
        public static bool Busy => busy;
        public static bool InSession => activeSession != 0 && connectedSession == activeSession;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Boot()
        {
            BasisSteamLobbyService.JoinRequested -= OnJoinRequested;
            BasisSteamLobbyService.JoinRequested += OnJoinRequested;
            BasisSteamLobbyService.HostLeft -= OnHostLeft;
            BasisSteamLobbyService.HostLeft += OnHostLeft;
            BasisNetworkPlayer.OnLocalPlayerJoined -= OnLocalPlayerJoined;
            BasisNetworkPlayer.OnLocalPlayerJoined += OnLocalPlayerJoined;
            BasisNetworkPlayer.OnLocalPlayerLeft -= OnLocalPlayerLeft;
            BasisNetworkPlayer.OnLocalPlayerLeft += OnLocalPlayerLeft;
            BasisNetworkConnection.OnRebootComplete -= CheckSessionEnded;
            BasisNetworkConnection.OnRebootComplete += CheckSessionEnded;
            BasisNetworkConnectionWatchdog.OnHealthChanged -= OnHealthChanged;
            BasisNetworkConnectionWatchdog.OnHealthChanged += OnHealthChanged;
            if (!BasisSteamClient.TryGetLaunchLobby(out ulong lobbyId))
            {
                return;
            }
            launchLobby = lobbyId;
            BasisConnectionService.AutoConnectAttempted = true;
            if (BasisNetworkManagement.IsInitialized)
            {
                JoinLaunchLobby();
            }
            else
            {
                BasisNetworkManagement.OnIstanceCreated -= JoinLaunchLobby;
                BasisNetworkManagement.OnIstanceCreated += JoinLaunchLobby;
            }
        }
        public static async Task<bool> HostAsync(BasisSteamHostRequest request, string userName)
        {
            if (busy || request == null)
            {
                return false;
            }
            busy = true;
            try
            {
                if (BasisConnectionService.ConnectInProgress)
                {
                    return Fail(BasisLocalization.Get("steam.error.busy"));
                }
                if (!PrepareUserName(ref userName))
                {
                    return false;
                }
                if (!BasisSteamLobbyService.EnsureReady())
                {
                    return Fail(BasisSteamLobbyService.LastError);
                }
                BasisSteamWorldCheck world = null;
                if (!string.IsNullOrWhiteSpace(request.WorldUrl))
                {
                    Status(BasisLocalization.Get("steam.status.checkingWorld"));
                    world = await BasisSteamWorld.ValidateAsync(request.WorldUrl, request.WorldPassword);
                    if (!world.IsValid)
                    {
                        return Fail(world.Error);
                    }
                }
                BasisSteamLobbyService.Leave();
                int session = ++sessionCounter;
                activeSession = session;
                string lobbyName = string.IsNullOrWhiteSpace(request.LobbyName) ? BasisLocalization.Get("steam.lobby.defaultName", SteamClient.Name) : request.LobbyName.Trim();
                int maxMembers = Mathf.Clamp(request.MaxMembers, BasisSteamLobbyService.MinMembers, BasisSteamLobbyService.MaxMembers);
                pendingHost = new PendingHost
                {
                    Session = session,
                    Name = lobbyName,
                    MaxMembers = maxMembers,
                    Visibility = request.Visibility,
                    World = world,
                    SessionKey = CreateSessionKey(),
                };
                ushort port = FindFreePort();
                BasisNetworkManagement.HostServerName = lobbyName;
                BasisNetworkManagement.HostServerMotd = string.Empty;
                BasisNetworkManagement.HostPeerLimit = maxMembers;
                BasisNetworkManagement.HostUseAuth = true;
                BasisNetworkManagement.HostAvatarsLocked = false;
                BasisNetworkManagement.HostPropsLocked = false;
                BasisNetworkManagement.HostWorldsLocked = true;
                BasisNetworkManagement.HostThirdPersonDisabled = false;
                ConnectionTarget target = new ConnectionTarget(HostStacks, "localhost:" + port.ToString(CultureInfo.InvariantCulture));
                target.Set(ConnectionTarget.Keys.Address, "localhost");
                target.Set(ConnectionTarget.Keys.Port, port.ToString(CultureInfo.InvariantCulture));
                ServerDirectoryEntry entry = new ServerDirectoryEntry
                {
                    Id = "steam-host",
                    SourceId = EntrySourceId,
                    DisplayName = lobbyName,
                    Password = pendingHost.SessionKey,
                    HasPassword = true,
                    Target = target,
                };
                Status(BasisLocalization.Get("steam.status.startingHost"));
                BasisMainMenu.Close();
                BasisCursorManagement.OnReset();
                await BasisConnectionService.ConnectAsync(entry, userName, true);
                return true;
            }
            finally
            {
                busy = false;
            }
        }
        public static async Task<bool> JoinAsync(ulong lobbyId, string userName)
        {
            if (busy || lobbyId == 0)
            {
                return false;
            }
            busy = true;
            try
            {
                if (BasisConnectionService.ConnectInProgress)
                {
                    return Fail(BasisLocalization.Get("steam.error.busy"));
                }
                if (!PrepareUserName(ref userName))
                {
                    return false;
                }
                if (!BasisSteamLobbyService.EnsureReady())
                {
                    return Fail(BasisSteamLobbyService.LastError);
                }
                Status(BasisLocalization.Get("steam.status.joining"));
                BasisSteamLobbyState state = await BasisSteamLobbyService.JoinAsync(lobbyId);
                if (state == null)
                {
                    return Fail(BasisSteamLobbyService.LastError);
                }
                if (state.IsHost)
                {
                    if (InSession)
                    {
                        return true;
                    }
                    BasisSteamLobbyService.Leave();
                    return Fail(BasisLocalization.Get("steam.error.ownLobby"));
                }
                if (!state.IsCompatible)
                {
                    BasisSteamLobbyService.Leave();
                    return Fail(BasisLocalization.Get("steam.error.version", state.Version, BasisNetworkVersion.ServerVersion));
                }
                if (state.HostSteamId == 0)
                {
                    BasisSteamLobbyService.Leave();
                    return Fail(BasisLocalization.Get("steam.error.noHost"));
                }
                activeSession = ++sessionCounter;
                pendingHost = null;
                ConnectionTarget target = new ConnectionTarget(BasisSteamTransport.StackId, BasisSteamConnectionTargetParser.Format(state.HostSteamId, state.VirtualPort));
                target.Set(ConnectionTarget.Keys.Address, state.HostSteamId.ToString(CultureInfo.InvariantCulture));
                target.Set(ConnectionTarget.Keys.Port, state.VirtualPort.ToString(CultureInfo.InvariantCulture));
                ServerDirectoryEntry entry = new ServerDirectoryEntry
                {
                    Id = "steam-lobby-" + state.LobbyId.ToString(CultureInfo.InvariantCulture),
                    SourceId = EntrySourceId,
                    DisplayName = state.LobbyName,
                    Password = state.SessionKey,
                    HasPassword = !string.IsNullOrEmpty(state.SessionKey),
                    Target = target,
                };
                BasisMainMenu.Close();
                BasisCursorManagement.OnReset();
                await BasisConnectionService.ConnectAsync(entry, userName, false, state.Version == BasisNetworkVersion.LegacyLoginVersion);
                return true;
            }
            finally
            {
                busy = false;
            }
        }
        public static async Task LeaveAsync()
        {
            bool connected = InSession && BasisNetworkConnection.LocalPlayerIsConnected;
            activeSession = 0;
            pendingHost = null;
            BasisSteamLobbyService.Leave();
            if (!connected)
            {
                return;
            }
            connectedSession = 0;
            using CancellationTokenSource cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task rebootWait = BasisNetworkConnection.WaitForRebootCompleteAsync(cancel.Token);
            await BasisNetworkLifeCycle.Destroy();
            try
            {
                await rebootWait;
            }
            catch (OperationCanceledException)
            {
                BasisDebug.LogWarning("Leaving the Steam session did not finish the network reboot in time.", BasisDebug.LogTag.Networking);
            }
            BasisNetworkLifeCycle.Initialize();
        }
        private static void OnLocalPlayerJoined(BasisNetworkPlayer player, BasisLocalPlayer local)
        {
            if (activeSession == 0)
            {
                if (BasisNetworkConnection.LocalPlayerPeer is BasisSteamNetPeer steamPeer && !BasisSteamLobbyService.InLobby && !busy)
                {
                    _ = RejoinHostLobbyAsync(steamPeer.RemoteSteamId);
                }
                return;
            }
            PendingHost host = pendingHost;
            bool hosting = BasisNetworkConnection.BasisNetworkServerRunner != null;
            bool overSteam = string.Equals(BasisNetworkConnection.LocalPlayerPeer?.StackId, BasisSteamTransport.StackId, StringComparison.OrdinalIgnoreCase);
            if (host != null ? !hosting : !overSteam)
            {
                EndSession();
                return;
            }
            connectedSession = activeSession;
            if (host != null && host.Session == activeSession)
            {
                pendingHost = null;
                _ = PublishHostAsync(host);
            }
        }
        private static async Task RejoinHostLobbyAsync(ulong hostSteamId)
        {
            if (busy || !BasisSteamLobbyService.EnsureReady())
            {
                return;
            }
            Friend.FriendGameInfo? info = new Friend(hostSteamId).GameInfo;
            if (!info.HasValue || !info.Value.Lobby.HasValue)
            {
                return;
            }
            busy = true;
            try
            {
                BasisSteamLobbyState state = await BasisSteamLobbyService.JoinAsync(info.Value.Lobby.Value.Id.Value);
                if (state == null)
                {
                    return;
                }
                bool stillConnected = BasisNetworkConnection.LocalPlayerPeer is BasisSteamNetPeer peer && peer.RemoteSteamId == hostSteamId;
                if (state.HostSteamId != hostSteamId || activeSession != 0 || !stillConnected)
                {
                    BasisSteamLobbyService.Leave();
                    return;
                }
                activeSession = ++sessionCounter;
                connectedSession = activeSession;
            }
            finally
            {
                busy = false;
            }
        }
        private static async Task PublishHostAsync(PendingHost host)
        {
            if (host.World != null && !BasisNetworkSpawnItem.RequestSceneLoad(host.World.Password, host.World.Url, true, true, out _))
            {
                BasisDebug.LogError("The Steam lobby world could not be requested.", BasisDebug.LogTag.Networking);
            }
            Status(BasisLocalization.Get("steam.status.publishing"));
            BasisSteamLobbyState state = await BasisSteamLobbyService.CreateAsync(host.Name, host.Visibility, host.MaxMembers, host.SessionKey, BasisSteamTransport.Config.VirtualPort, host.World?.Url, host.World?.Name);
            if (state == null)
            {
                Fail(BasisLocalization.Get("steam.error.publishFailed", BasisSteamLobbyService.LastError));
                return;
            }
            if (activeSession != host.Session)
            {
                BasisSteamLobbyService.Leave();
                return;
            }
            Status(BasisLocalization.Get("steam.status.ready"));
        }
        private static void OnLocalPlayerLeft(BasisNetworkPlayer player, BasisLocalPlayer local)
        {
            int ended = connectedSession;
            connectedSession = 0;
            if (ended == 0 || ended != activeSession || BasisNetworkConnectionWatchdog.Health == BasisConnectionHealth.Reconnecting)
            {
                return;
            }
            EndSession();
        }
        private static void OnHealthChanged(BasisConnectionHealth health)
        {
            if (health == BasisConnectionHealth.Idle)
            {
                CheckSessionEnded();
            }
        }
        private static void CheckSessionEnded()
        {
            if (activeSession == 0 || connectedSession != 0 || busy || BasisConnectionService.ConnectInProgress || BasisNetworkConnection.LocalPlayerIsConnected)
            {
                return;
            }
            if (BasisNetworkConnectionWatchdog.Health == BasisConnectionHealth.Reconnecting || BasisNetworkConnectionWatchdog.Health == BasisConnectionHealth.Connecting)
            {
                return;
            }
            EndSession();
        }
        private static void EndSession()
        {
            activeSession = 0;
            pendingHost = null;
            BasisSteamLobbyService.Leave();
        }
        private static void OnHostLeft()
        {
            if (activeSession != 0 && !(BasisSteamLobbyService.Current?.IsHost ?? false))
            {
                activeSession = 0;
                pendingHost = null;
            }
            Status(BasisLocalization.Get("steam.status.hostLeft"));
        }
        private static void OnJoinRequested(ulong lobbyId)
        {
            _ = JoinAsync(lobbyId, null);
        }
        private static void JoinLaunchLobby()
        {
            BasisNetworkManagement.OnIstanceCreated -= JoinLaunchLobby;
            ulong lobbyId = launchLobby;
            launchLobby = 0;
            if (lobbyId != 0)
            {
                _ = JoinAsync(lobbyId, null);
            }
        }
        private static bool PrepareUserName(ref string userName)
        {
            if (string.IsNullOrWhiteSpace(userName))
            {
                userName = BasisDataStore.LoadString(BasisConnectionService.UsernameFileName, string.Empty);
            }
            if (string.IsNullOrWhiteSpace(userName) && SteamClient.IsValid)
            {
                userName = SteamClient.Name;
            }
            if (string.IsNullOrWhiteSpace(userName))
            {
                return Fail(BasisLocalization.Get("steam.error.username"));
            }
            userName = userName.Trim();
            return true;
        }
        private static string CreateSessionKey()
        {
            byte[] bytes = new byte[16];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            for (int index = 0; index < bytes.Length; index++)
            {
                builder.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
        private static ushort FindFreePort()
        {
            try
            {
                using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                return (ushort)((IPEndPoint)socket.LocalEndPoint).Port;
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Could not reserve a free UDP port for the Steam host, using the default: {ex.Message}", BasisDebug.LogTag.Networking);
                return HostPortConnectionTargetParser.DefaultPort;
            }
        }
        private static void Status(string message)
        {
            try
            {
                StatusChanged?.Invoke(message);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Steam session status listener failed: {ex}", BasisDebug.LogTag.Networking);
            }
        }
        private static bool Fail(string message)
        {
            LastError = string.IsNullOrWhiteSpace(message) ? BasisLocalization.Get("steam.error.generic") : message;
            BasisDebug.LogWarning(LastError, BasisDebug.LogTag.Networking);
            if (Failed != null)
            {
                try
                {
                    Failed(LastError);
                }
                catch (Exception ex)
                {
                    BasisDebug.LogError($"Steam session failure listener failed: {ex}", BasisDebug.LogTag.Networking);
                }
            }
            else
            {
                BasisConnectionService.ReportConnectionError(LastError);
            }
            return false;
        }
    }
}
