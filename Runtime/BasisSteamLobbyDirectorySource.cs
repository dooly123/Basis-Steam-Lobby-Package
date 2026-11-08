using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Basis.Network.Steam
{
    [AutoStaticsCleanup]
    public sealed partial class BasisSteamLobbyDirectorySource : IServerDirectorySource
    {
        public const string Id = "steamlobby";
        public const string EntryIdPrefix = "steamlobby:";
        public const int StaleAfterMs = 30000;
        private static readonly long StaleAfterTicks = (long)(StaleAfterMs / 1000.0 * Stopwatch.Frequency);
        public static BasisSteamLobbyDirectorySource Instance;
        private readonly List<ServerDirectoryEntry> entries = new List<ServerDirectoryEntry>();
        private long searchedTicks;
        private bool searching, searched;
        public string SourceId => Id;
        public string DisplayName => BasisLocalization.Get("steam.lobbies.source");
        public bool SupportsAdd => false;
        public event Action SourceChanged;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void Boot()
        {
            if (Instance != null)
            {
                return;
            }
            Instance = new BasisSteamLobbyDirectorySource();
            BasisServerDirectoryRegistry.Register(Instance);
            BasisSteamClient.Initialized -= OnSteamInitialized;
            BasisSteamClient.Initialized += OnSteamInitialized;
        }
        private static void OnSteamInitialized()
        {
            Instance?.Search();
        }
        public Task<IReadOnlyList<ServerDirectoryEntry>> ListAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SteamReady())
            {
                return Task.FromResult<IReadOnlyList<ServerDirectoryEntry>>(Array.Empty<ServerDirectoryEntry>());
            }
            if (!searched || Stopwatch.GetTimestamp() - searchedTicks > StaleAfterTicks)
            {
                Search();
            }
            return Task.FromResult<IReadOnlyList<ServerDirectoryEntry>>(entries.ToArray());
        }
        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Search();
            return Task.CompletedTask;
        }
        private static bool SteamReady()
        {
            return SteamClient.IsValid && SteamClient.IsLoggedOn && BasisSteamClient.IsMainThread;
        }
        private void Search()
        {
            if (searching || !SteamReady())
            {
                return;
            }
            _ = SearchAsync();
        }
        private async Task SearchAsync()
        {
            searching = true;
            searchedTicks = Stopwatch.GetTimestamp();
            List<BasisSteamLobbyState> found;
            try
            {
                found = await BasisSteamLobbyService.SearchAsync();
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Steam lobby search for the server list failed: {ex.Message}", BasisDebug.LogTag.Networking);
                return;
            }
            finally
            {
                searching = false;
            }
            if (Instance != this)
            {
                return;
            }
            searched = true;
            searchedTicks = Stopwatch.GetTimestamp();
            ulong self = SteamClient.IsValid ? SteamClient.SteamId.Value : 0;
            entries.Clear();
            for (int index = 0; index < found.Count; index++)
            {
                BasisSteamLobbyState lobby = found[index];
                if (lobby.HostSteamId == 0 || lobby.HostSteamId == self)
                {
                    continue;
                }
                entries.Add(BuildEntry(lobby));
            }
            try
            {
                SourceChanged?.Invoke();
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Steam lobby SourceChanged handler threw: {ex.Message}", BasisDebug.LogTag.Networking);
            }
        }
        private static ServerDirectoryEntry BuildEntry(BasisSteamLobbyState lobby)
        {
            string host = lobby.HostSteamId.ToString(CultureInfo.InvariantCulture);
            string port = lobby.VirtualPort.ToString(CultureInfo.InvariantCulture);
            ConnectionTarget target = new ConnectionTarget(BasisSteamTransport.StackId, BasisSteamConnectionTargetParser.Format(lobby.HostSteamId, lobby.VirtualPort));
            target.Set(ConnectionTarget.Keys.Address, host);
            target.Set(ConnectionTarget.Keys.Port, port);
            string world = string.IsNullOrWhiteSpace(lobby.WorldName) ? BasisLocalization.Get("steam.lobbies.details.noWorld") : lobby.WorldName;
            string label = BasisLocalization.Get("steam.lobbies.servers.address", lobby.HostName, world);
            if (!lobby.IsCompatible)
            {
                label += "\n" + BasisLocalization.Get("steam.lobbies.details.incompatible", lobby.Version, BasisNetworkVersion.ServerVersion);
            }
            ulong lobbyId = lobby.LobbyId;
            return new ServerDirectoryEntry
            {
                Id = EntryIdPrefix + lobbyId.ToString(CultureInfo.InvariantCulture),
                SourceId = Id,
                DisplayName = string.IsNullOrWhiteSpace(lobby.LobbyName) ? BasisLocalization.Get("steam.lobby.defaultName", lobby.HostName) : lobby.LobbyName,
                Description = label,
                AddressLabel = label,
                HasPassword = false,
                Password = string.Empty,
                Target = target,
                CanEdit = false,
                CanRemove = false,
                CanAutoConnect = false,
                Join = userName => BasisSteamSession.JoinAsync(lobbyId, userName),
            };
        }
    }
}
