using Basis.Network.Core;
using System;

namespace Basis.Network.Steam
{
    public enum BasisSteamLobbyVisibility
    {
        FriendsOnly,
        Public,
        Private,
    }
    public static class BasisSteamLobbyMetadata
    {
        public const string Marker = "basis_lobby";
        public const string MarkerValue = "1";
        public const string Version = "basis_version";
        public const string Name = "basis_name";
        public const string HostName = "basis_host_name";
        public const string HostSteamId = "basis_host";
        public const string VirtualPort = "basis_virtual_port";
        public const string SessionKey = "basis_session_key";
        public const string WorldUrl = "basis_world_url";
        public const string WorldName = "basis_world_name";
        public const string Members = "basis_members";
    }
    [Serializable]
    public sealed class BasisSteamLobbyState
    {
        public ulong LobbyId, HostSteamId;
        public string LobbyName = string.Empty, HostName = string.Empty, WorldUrl = string.Empty, WorldName = string.Empty, SessionKey = string.Empty;
        public int VirtualPort, MemberCount, MaxMembers;
        public ushort Version;
        public bool IsHost;
        public bool IsCompatible => Version == BasisNetworkVersion.ServerVersion || Version == BasisNetworkVersion.LegacyLoginVersion;
        public BasisSteamLobbyState Clone()
        {
            return (BasisSteamLobbyState)MemberwiseClone();
        }
    }
}
