using Basis.BasisUI;
using Basis.Network.Core;
using Steamworks;
using Steamworks.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Network.Steam
{
    [AutoStaticsCleanup]
    public static partial class BasisSteamLobbyService
    {
        public const int MinMembers = 2, MaxMembers = 250, MaxSearchResults = 50, FriendLobbyWaitSteps = 20, FriendLobbyWaitStepMs = 100;
        public static BasisSteamLobbyState Current;
        public static string LastError = string.Empty;
        public static event Action<BasisSteamLobbyState> LobbyChanged;
        public static event Action<ulong> JoinRequested;
        public static event Action HostLeft;
        private static Lobby? lobby;
        public static bool InLobby => lobby.HasValue;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Boot()
        {
            BasisSteamClient.Initialized -= Subscribe;
            BasisSteamClient.Initialized += Subscribe;
            BasisSteamClient.ShuttingDown -= OnSteamShuttingDown;
            BasisSteamClient.ShuttingDown += OnSteamShuttingDown;
            if (SteamClient.IsValid)
            {
                Subscribe();
            }
        }
        public static bool EnsureReady()
        {
            if (!BasisSteamClient.EnsureInitialized())
            {
                return Fail(BasisLocalization.Get("steam.error.unavailable", BasisSteamClient.LastError));
            }
            if (!SteamClient.IsLoggedOn)
            {
                return Fail(BasisLocalization.Get("steam.error.loggedOut"));
            }
            Subscribe();
            return true;
        }
        public static async Task<BasisSteamLobbyState> CreateAsync(string name, BasisSteamLobbyVisibility visibility, int maxMembers, string sessionKey, int virtualPort, string worldUrl, string worldName)
        {
            if (!EnsureReady())
            {
                return null;
            }
            Leave();
            Lobby? created = await SteamMatchmaking.CreateLobbyAsync(Mathf.Clamp(maxMembers, MinMembers, MaxMembers));
            if (!created.HasValue)
            {
                Fail(BasisLocalization.Get("steam.error.createFailed"));
                return null;
            }
            Lobby value = created.Value;
            value.SetData(BasisSteamLobbyMetadata.Marker, BasisSteamLobbyMetadata.MarkerValue);
            value.SetData(BasisSteamLobbyMetadata.Version, BasisNetworkVersion.ServerVersion.ToString(CultureInfo.InvariantCulture));
            value.SetData(BasisSteamLobbyMetadata.Name, string.IsNullOrWhiteSpace(name) ? SteamClient.Name : name.Trim());
            value.SetData(BasisSteamLobbyMetadata.HostName, SteamClient.Name);
            value.SetData(BasisSteamLobbyMetadata.HostSteamId, SteamClient.SteamId.Value.ToString(CultureInfo.InvariantCulture));
            value.SetData(BasisSteamLobbyMetadata.VirtualPort, virtualPort.ToString(CultureInfo.InvariantCulture));
            value.SetData(BasisSteamLobbyMetadata.SessionKey, sessionKey ?? string.Empty);
            value.SetData(BasisSteamLobbyMetadata.WorldUrl, worldUrl ?? string.Empty);
            value.SetData(BasisSteamLobbyMetadata.WorldName, worldName ?? string.Empty);
            value.SetData(BasisSteamLobbyMetadata.Members, "1");
            switch (visibility)
            {
                case BasisSteamLobbyVisibility.Public:
                    value.SetPublic();
                    break;
                case BasisSteamLobbyVisibility.Private:
                    value.SetPrivate();
                    break;
                default:
                    value.SetFriendsOnly();
                    break;
            }
            value.SetJoinable(true);
            lobby = value;
            Apply(value);
            return Current.Clone();
        }
        public static async Task<BasisSteamLobbyState> JoinAsync(ulong lobbyId)
        {
            if (!EnsureReady())
            {
                return null;
            }
            if (lobby.HasValue && lobby.Value.Id.Value == lobbyId && Current != null)
            {
                return Current.Clone();
            }
            Leave();
            Lobby? joined = await SteamMatchmaking.JoinLobbyAsync(lobbyId);
            if (!joined.HasValue)
            {
                Fail(BasisLocalization.Get("steam.error.joinFailed"));
                return null;
            }
            lobby = joined.Value;
            Apply(joined.Value);
            return Current.Clone();
        }
        public static async Task<List<BasisSteamLobbyState>> SearchAsync()
        {
            List<BasisSteamLobbyState> results = new List<BasisSteamLobbyState>();
            if (!EnsureReady())
            {
                return results;
            }
            HashSet<ulong> seen = new HashSet<ulong>();
            Lobby[] found = await SteamMatchmaking.LobbyList.FilterDistanceWorldwide().WithKeyValue(BasisSteamLobbyMetadata.Marker, BasisSteamLobbyMetadata.MarkerValue).WithMaxResults(MaxSearchResults).RequestAsync();
            if (found != null)
            {
                for (int index = 0; index < found.Length; index++)
                {
                    if (seen.Add(found[index].Id.Value))
                    {
                        results.Add(Read(found[index]));
                    }
                }
            }
            List<Lobby> friendLobbies = new List<Lobby>();
            foreach (Friend friend in SteamFriends.GetFriends())
            {
                if (!friend.IsPlayingThisGame)
                {
                    continue;
                }
                Friend.FriendGameInfo? info = friend.GameInfo;
                if (info.HasValue && info.Value.Lobby.HasValue && seen.Add(info.Value.Lobby.Value.Id.Value))
                {
                    Lobby friendLobby = info.Value.Lobby.Value;
                    friendLobby.Refresh();
                    friendLobbies.Add(friendLobby);
                }
            }
            for (int attempt = 0; attempt < FriendLobbyWaitSteps && !HaveData(friendLobbies); attempt++)
            {
                await Task.Delay(FriendLobbyWaitStepMs);
            }
            for (int index = 0; index < friendLobbies.Count; index++)
            {
                if (friendLobbies[index].GetData(BasisSteamLobbyMetadata.Marker) == BasisSteamLobbyMetadata.MarkerValue)
                {
                    results.Add(Read(friendLobbies[index]));
                }
            }
            results.Sort((a, b) => string.Compare(a.LobbyName, b.LobbyName, StringComparison.OrdinalIgnoreCase));
            return results;
        }
        private static bool HaveData(List<Lobby> lobbies)
        {
            for (int index = 0; index < lobbies.Count; index++)
            {
                if (string.IsNullOrEmpty(lobbies[index].GetData(BasisSteamLobbyMetadata.Marker)))
                {
                    return false;
                }
            }
            return true;
        }
        public static void Leave()
        {
            if (!lobby.HasValue)
            {
                return;
            }
            if (SteamClient.IsValid)
            {
                lobby.Value.Leave();
            }
            lobby = null;
            Current = null;
            Raise();
        }
        public static bool OpenInviteOverlay()
        {
            if (!lobby.HasValue || !EnsureReady())
            {
                return false;
            }
            SteamFriends.OpenGameInviteOverlay(lobby.Value.Id);
            return true;
        }
        public static bool Invite(ulong friendSteamId)
        {
            return lobby.HasValue && SteamClient.IsValid && lobby.Value.InviteFriend(friendSteamId);
        }
        public static List<Friend> OnlineFriends()
        {
            List<Friend> friends = new List<Friend>();
            if (!SteamClient.IsValid)
            {
                return friends;
            }
            foreach (Friend friend in SteamFriends.GetFriends())
            {
                if (friend.IsOnline)
                {
                    friends.Add(friend);
                }
            }
            friends.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return friends;
        }
        public static void SetWorld(string worldUrl, string worldName)
        {
            if (!lobby.HasValue || Current == null || !Current.IsHost)
            {
                return;
            }
            Lobby value = lobby.Value;
            value.SetData(BasisSteamLobbyMetadata.WorldUrl, worldUrl ?? string.Empty);
            value.SetData(BasisSteamLobbyMetadata.WorldName, worldName ?? string.Empty);
            Apply(value);
        }
        private static BasisSteamLobbyState Read(Lobby source)
        {
            ulong host = ParseUlong(source.GetData(BasisSteamLobbyMetadata.HostSteamId));
            return new BasisSteamLobbyState
            {
                LobbyId = source.Id.Value,
                HostSteamId = host,
                LobbyName = source.GetData(BasisSteamLobbyMetadata.Name) ?? string.Empty,
                HostName = source.GetData(BasisSteamLobbyMetadata.HostName) ?? string.Empty,
                WorldUrl = source.GetData(BasisSteamLobbyMetadata.WorldUrl) ?? string.Empty,
                WorldName = source.GetData(BasisSteamLobbyMetadata.WorldName) ?? string.Empty,
                SessionKey = source.GetData(BasisSteamLobbyMetadata.SessionKey) ?? string.Empty,
                VirtualPort = (int)ParseUlong(source.GetData(BasisSteamLobbyMetadata.VirtualPort)),
                Version = (ushort)ParseUlong(source.GetData(BasisSteamLobbyMetadata.Version)),
                MemberCount = Math.Max(source.MemberCount, (int)Math.Min(ParseUlong(source.GetData(BasisSteamLobbyMetadata.Members)), MaxMembers)),
                MaxMembers = source.MaxMembers,
                IsHost = host != 0 && SteamClient.IsValid && host == SteamClient.SteamId.Value,
            };
        }
        private static void Apply(Lobby source)
        {
            Current = Read(source);
            Raise();
        }
        private static void Raise()
        {
            try
            {
                LobbyChanged?.Invoke(Current?.Clone());
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Steam lobby listener failed: {ex}", BasisDebug.LogTag.Networking);
            }
        }
        private static bool Fail(string message)
        {
            LastError = message ?? string.Empty;
            BasisDebug.LogWarning(LastError, BasisDebug.LogTag.Networking);
            return false;
        }
        private static ulong ParseUlong(string value)
        {
            return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed) ? parsed : 0;
        }
        private static void Subscribe()
        {
            SteamFriends.OnGameLobbyJoinRequested -= OnGameLobbyJoinRequested;
            SteamFriends.OnGameLobbyJoinRequested += OnGameLobbyJoinRequested;
            SteamMatchmaking.OnLobbyDataChanged -= OnLobbyDataChanged;
            SteamMatchmaking.OnLobbyDataChanged += OnLobbyDataChanged;
            SteamMatchmaking.OnLobbyMemberJoined -= OnLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberJoined += OnLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberGone;
            SteamMatchmaking.OnLobbyMemberLeave += OnLobbyMemberGone;
            SteamMatchmaking.OnLobbyMemberDisconnected -= OnLobbyMemberGone;
            SteamMatchmaking.OnLobbyMemberDisconnected += OnLobbyMemberGone;
        }
        private static void Unsubscribe()
        {
            SteamFriends.OnGameLobbyJoinRequested -= OnGameLobbyJoinRequested;
            SteamMatchmaking.OnLobbyDataChanged -= OnLobbyDataChanged;
            SteamMatchmaking.OnLobbyMemberJoined -= OnLobbyMemberJoined;
            SteamMatchmaking.OnLobbyMemberLeave -= OnLobbyMemberGone;
            SteamMatchmaking.OnLobbyMemberDisconnected -= OnLobbyMemberGone;
        }
        private static void OnSteamShuttingDown()
        {
            Leave();
            Unsubscribe();
        }
        private static bool IsCurrent(Lobby other)
        {
            return lobby.HasValue && lobby.Value.Id.Value == other.Id.Value;
        }
        private static void OnGameLobbyJoinRequested(Lobby target, SteamId friend)
        {
            JoinRequested?.Invoke(target.Id.Value);
        }
        private static void OnLobbyDataChanged(Lobby changed)
        {
            if (IsCurrent(changed))
            {
                Apply(changed);
            }
        }
        private static void OnLobbyMemberJoined(Lobby changed, Friend member)
        {
            if (IsCurrent(changed))
            {
                UpdateMembers(changed);
                Apply(changed);
            }
        }
        private static void UpdateMembers(Lobby changed)
        {
            if (Current == null || !Current.IsHost)
            {
                return;
            }
            string count = changed.MemberCount.ToString(CultureInfo.InvariantCulture);
            if (changed.GetData(BasisSteamLobbyMetadata.Members) != count)
            {
                changed.SetData(BasisSteamLobbyMetadata.Members, count);
            }
        }
        private static void OnLobbyMemberGone(Lobby changed, Friend member)
        {
            if (!IsCurrent(changed))
            {
                return;
            }
            if (Current != null && !Current.IsHost && member.Id.Value == Current.HostSteamId)
            {
                Leave();
                HostLeft?.Invoke();
                return;
            }
            UpdateMembers(changed);
            Apply(changed);
        }
    }
}
