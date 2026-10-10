using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.Common;
using Basis.Scripts.Networking;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Network.Steam
{
    public class SteamLobbiesProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        public const string TitleKey = "steam.lobbies.title";
        private static readonly int[] memberChoices = { 4, 8, 16, 32, 64 };
        private readonly List<string> visibilityEntries = new List<string> { nameof(BasisSteamLobbyVisibility.FriendsOnly), nameof(BasisSteamLobbyVisibility.Public), nameof(BasisSteamLobbyVisibility.Private) };
        private readonly List<string> visibilityKeys = new List<string> { "steam.lobbies.visibility.friendsOnly", "steam.lobbies.visibility.public", "steam.lobbies.visibility.private" };
        private readonly List<BasisSteamLobbyState> lobbies = new List<BasisSteamLobbyState>();
        private readonly List<Friend> friends = new List<Friend>();
        private BasisMenuPanel panel;
        private PanelElementDescriptor statusGroup, hostGroup, browseGroup, sessionGroup, lobbyDetails;
        private PanelTextField usernameField, lobbyNameField, worldUrlField;
        private PanelPasswordField worldPasswordField;
        private PanelDropdown visibilityDropdown, membersDropdown, lobbyDropdown, friendDropdown;
        private PanelButton hostButton, refreshButton, joinButton, overlayButton, inviteButton, leaveButton;
        private bool searching;
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            BasisMenuBase<BasisMainMenu>.AddProvider(new SteamLobbiesProvider());
        }
        public override string Title => BasisLocalization.Get(TitleKey);
        public override string IconAddress => AddressableAssets.Sprites.Servers;
        public override int Order => 31;
        public override bool Hidden => false;
        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.CloseActivePanel();
                return;
            }
            panel = BasisMainMenu.CreateActiveMenu(BasisMenuPanel.PanelData.Standard(Title), BasisMenuPanel.PanelStyles.Page);
            BoundButton?.BindActiveStateToAddressablesInstance(panel);
            panel.OnInstanceReleased += OnPanelClosed;
            PanelTabPage page = PanelTabPage.CreateVertical(panel.Descriptor.ContentParent);
            page.Descriptor.SetTitle(Title);
            page.Descriptor.SetIcon(AddressableAssets.Sprites.Servers);
            Build(page.Descriptor.ContentParent);
            BasisSteamLobbyService.LobbyChanged += OnLobbyChanged;
            BasisSteamSession.StatusChanged += SetStatus;
            BasisSteamSession.Failed += SetStatus;
            if (BasisSteamLobbyService.EnsureReady())
            {
                SetStatus(BasisLocalization.Get("steam.lobbies.status.ready", SteamClient.Name));
                if (string.IsNullOrWhiteSpace(Text(usernameField)))
                {
                    usernameField.SetValueWithoutNotify(SteamClient.Name);
                }
                if (!BasisSteamLobbyService.InLobby)
                {
                    _ = SearchAsync();
                }
            }
            else
            {
                SetStatus(BasisSteamLobbyService.LastError);
            }
            ApplyState();
            panel.Descriptor.ForceRebuild();
        }
        private void Build(RectTransform container)
        {
            statusGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            statusGroup.SetTitle(BasisLocalization.Get("steam.lobbies.status.title"));
            usernameField = PanelTextField.CreateNewEntry(container);
            usernameField.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.username"));
            usernameField.SetValueWithoutNotify(BasisDataStore.LoadString(BasisConnectionService.UsernameFileName, string.Empty));
            hostGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            hostGroup.SetTitle(BasisLocalization.Get("steam.lobbies.host.title"));
            hostGroup.SetDescription(BasisLocalization.Get("steam.lobbies.host.description"));
            RectTransform hostContent = hostGroup.ContentParent;
            lobbyNameField = PanelTextField.CreateNewEntry(hostContent);
            lobbyNameField.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.host.name"));
            visibilityDropdown = PanelDropdown.CreateNewEntry(hostContent);
            visibilityDropdown.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.host.visibility"));
            visibilityDropdown.AssignLocalizedEntries(visibilityEntries, visibilityKeys);
            visibilityDropdown.SetValueWithoutNotify(visibilityEntries[0]);
            membersDropdown = PanelDropdown.CreateNewEntry(hostContent);
            membersDropdown.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.host.members"));
            List<string> members = new List<string>(memberChoices.Length);
            for (int index = 0; index < memberChoices.Length; index++)
            {
                members.Add(memberChoices[index].ToString(CultureInfo.InvariantCulture));
            }
            membersDropdown.AssignEntries(members);
            membersDropdown.SetValueWithoutNotify(BasisSteamSession.DefaultMaxMembers.ToString(CultureInfo.InvariantCulture));
            worldUrlField = PanelTextField.CreateNewEntry(hostContent);
            worldUrlField.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.host.worldUrl"));
            worldPasswordField = PanelPasswordField.CreateNewEntry(hostContent);
            worldPasswordField.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.host.worldPassword"));
            hostButton = PanelButton.CreateNew(hostContent);
            hostButton.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.host.button"));
            hostButton.OnClicked += () => _ = HostAsync();
            browseGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            browseGroup.SetTitle(BasisLocalization.Get("steam.lobbies.browse.title"));
            browseGroup.SetDescription(BasisLocalization.Get("steam.lobbies.browse.description"));
            RectTransform browseContent = browseGroup.ContentParent;
            refreshButton = PanelButton.CreateNew(browseContent);
            refreshButton.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.browse.refresh"));
            refreshButton.OnClicked += () => _ = SearchAsync();
            lobbyDropdown = PanelDropdown.CreateNew(PanelDropdown.DropdownStyles.EntryNoLabel, browseContent);
            lobbyDropdown.OnValueChanged += _ => ShowSelectedLobby();
            lobbyDetails = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, browseContent);
            joinButton = PanelButton.CreateNew(browseContent);
            joinButton.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.browse.join"));
            joinButton.OnClicked += () => _ = JoinAsync();
            sessionGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            sessionGroup.SetTitle(BasisLocalization.Get("steam.lobbies.session.title"));
            RectTransform sessionContent = sessionGroup.ContentParent;
            overlayButton = PanelButton.CreateNew(sessionContent);
            overlayButton.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.session.overlay"));
            overlayButton.OnClicked += OpenOverlay;
            friendDropdown = PanelDropdown.CreateNewEntry(sessionContent);
            friendDropdown.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.session.friend"));
            inviteButton = PanelButton.CreateNew(sessionContent);
            inviteButton.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.session.invite"));
            inviteButton.OnClicked += InviteSelectedFriend;
            leaveButton = PanelButton.CreateNew(sessionContent);
            leaveButton.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.session.leave"));
            leaveButton.OnClicked += () => _ = LeaveAsync();
            FillLobbies();
        }
        private void OnPanelClosed()
        {
            BasisSteamLobbyService.LobbyChanged -= OnLobbyChanged;
            BasisSteamSession.StatusChanged -= SetStatus;
            BasisSteamSession.Failed -= SetStatus;
            string userName = Text(usernameField);
            if (!string.IsNullOrWhiteSpace(userName))
            {
                BasisDataStore.SaveString(userName.Trim(), BasisConnectionService.UsernameFileName);
            }
            panel = null;
            statusGroup = hostGroup = browseGroup = sessionGroup = lobbyDetails = null;
            usernameField = lobbyNameField = worldUrlField = null;
            worldPasswordField = null;
            visibilityDropdown = membersDropdown = lobbyDropdown = friendDropdown = null;
            hostButton = refreshButton = joinButton = overlayButton = inviteButton = leaveButton = null;
        }
        private bool IsOpen => panel != null;
        private static string Text(PanelTextField field)
        {
            if (field == null)
            {
                return string.Empty;
            }
            return field._inputField != null ? field._inputField.text : field.Value ?? string.Empty;
        }
        private async Task HostAsync()
        {
            BasisSteamHostRequest request = new BasisSteamHostRequest
            {
                LobbyName = Text(lobbyNameField),
                WorldUrl = Text(worldUrlField),
                WorldPassword = worldPasswordField.Password,
                Visibility = Enum.TryParse(visibilityDropdown.Value, out BasisSteamLobbyVisibility visibility) ? visibility : BasisSteamLobbyVisibility.FriendsOnly,
                MaxMembers = int.TryParse(membersDropdown.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxMembers) ? maxMembers : BasisSteamSession.DefaultMaxMembers,
            };
            hostButton.SetInteractable(false);
            try
            {
                await BasisSteamSession.HostAsync(request, Text(usernameField));
            }
            finally
            {
                if (IsOpen)
                {
                    ApplyState();
                }
            }
        }
        private async Task JoinAsync()
        {
            BasisSteamLobbyState selected = SelectedLobby();
            if (selected == null)
            {
                return;
            }
            joinButton.SetInteractable(false);
            try
            {
                await BasisSteamSession.JoinAsync(selected.LobbyId, Text(usernameField));
            }
            finally
            {
                if (IsOpen)
                {
                    ApplyState();
                }
            }
        }
        private async Task LeaveAsync()
        {
            leaveButton.SetInteractable(false);
            await BasisSteamSession.LeaveAsync();
            if (IsOpen)
            {
                SetStatus(BasisLocalization.Get("steam.lobbies.session.left"));
                ApplyState();
                _ = SearchAsync();
            }
        }
        private async Task SearchAsync()
        {
            if (searching)
            {
                return;
            }
            searching = true;
            refreshButton?.SetInteractable(false);
            lobbyDetails?.SetDescription(BasisLocalization.Get("steam.lobbies.browse.searching"));
            try
            {
                List<BasisSteamLobbyState> found = await BasisSteamLobbyService.SearchAsync();
                lobbies.Clear();
                lobbies.AddRange(found);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"Steam lobby search failed: {ex}", BasisDebug.LogTag.Networking);
            }
            finally
            {
                searching = false;
            }
            if (IsOpen)
            {
                FillLobbies();
                ApplyState();
            }
        }
        private void FillLobbies()
        {
            List<string> entries = new List<string>(lobbies.Count);
            List<string> labels = new List<string>(lobbies.Count);
            for (int index = 0; index < lobbies.Count; index++)
            {
                BasisSteamLobbyState lobby = lobbies[index];
                entries.Add(lobby.LobbyId.ToString(CultureInfo.InvariantCulture));
                labels.Add(BasisLocalization.Get("steam.lobbies.browse.entry", DisplayName(lobby), lobby.MemberCount, lobby.MaxMembers));
            }
            if (entries.Count == 0)
            {
                entries.Add(string.Empty);
                labels.Add(BasisLocalization.Get("steam.lobbies.browse.empty"));
            }
            lobbyDropdown.AssignEntries(entries, labels);
            lobbyDropdown.SetValueWithoutNotify(entries[0]);
            ShowSelectedLobby();
        }
        private BasisSteamLobbyState SelectedLobby()
        {
            if (lobbyDropdown == null || !ulong.TryParse(lobbyDropdown.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong lobbyId))
            {
                return null;
            }
            for (int index = 0; index < lobbies.Count; index++)
            {
                if (lobbies[index].LobbyId == lobbyId)
                {
                    return lobbies[index];
                }
            }
            return null;
        }
        private void ShowSelectedLobby()
        {
            if (lobbyDetails == null)
            {
                return;
            }
            BasisSteamLobbyState selected = SelectedLobby();
            if (selected == null)
            {
                lobbyDetails.SetTitle(string.Empty);
                lobbyDetails.SetDescription(BasisLocalization.Get("steam.lobbies.browse.none"));
            }
            else
            {
                lobbyDetails.SetTitle(DisplayName(selected));
                lobbyDetails.SetDescription(Describe(selected));
            }
            joinButton?.SetInteractable(selected != null && selected.IsCompatible && !BasisSteamSession.Busy);
        }
        private void OnLobbyChanged(BasisSteamLobbyState state)
        {
            if (IsOpen)
            {
                ApplyState();
            }
        }
        private void ApplyState()
        {
            BasisSteamLobbyState current = BasisSteamLobbyService.Current;
            bool inLobby = current != null;
            bool busy = BasisSteamSession.Busy;
            hostGroup.SetActive(!inLobby);
            browseGroup.SetActive(!inLobby);
            sessionGroup.SetActive(inLobby);
            hostButton.SetInteractable(!busy && BasisSteamClient.IsReady);
            refreshButton.SetInteractable(!searching && BasisSteamClient.IsReady);
            BasisSteamLobbyState selected = SelectedLobby();
            joinButton.SetInteractable(!busy && selected != null && selected.IsCompatible);
            leaveButton.SetInteractable(true);
            if (inLobby)
            {
                sessionGroup.SetTitle(DisplayName(current));
                sessionGroup.SetDescription(Describe(current));
                FillFriends();
            }
            panel?.Descriptor.ForceRebuild();
        }
        private void FillFriends()
        {
            friends.Clear();
            friends.AddRange(BasisSteamLobbyService.OnlineFriends());
            List<string> entries = new List<string>(friends.Count);
            List<string> labels = new List<string>(friends.Count);
            for (int index = 0; index < friends.Count; index++)
            {
                entries.Add(friends[index].Id.Value.ToString(CultureInfo.InvariantCulture));
                labels.Add(friends[index].Name);
            }
            if (entries.Count == 0)
            {
                entries.Add(string.Empty);
                labels.Add(BasisLocalization.Get("steam.lobbies.session.noFriends"));
            }
            string previous = friendDropdown.Value;
            friendDropdown.AssignEntries(entries, labels);
            friendDropdown.SetValueWithoutNotify(entries.Contains(previous) ? previous : entries[0]);
            inviteButton.SetInteractable(friends.Count > 0);
        }
        private void OpenOverlay()
        {
            if (!BasisSteamLobbyService.OpenInviteOverlay())
            {
                SetStatus(BasisLocalization.Get("steam.error.overlay"));
            }
        }
        private void InviteSelectedFriend()
        {
            if (!ulong.TryParse(friendDropdown.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong friendId))
            {
                return;
            }
            for (int index = 0; index < friends.Count; index++)
            {
                if (friends[index].Id.Value == friendId)
                {
                    SetStatus(BasisSteamLobbyService.Invite(friendId) ? BasisLocalization.Get("steam.lobbies.session.invited", friends[index].Name) : BasisLocalization.Get("steam.error.invite"));
                    return;
                }
            }
        }
        private void SetStatus(string message)
        {
            if (statusGroup != null)
            {
                statusGroup.SetDescription(message ?? string.Empty);
            }
        }
        private static string DisplayName(BasisSteamLobbyState lobby)
        {
            return string.IsNullOrWhiteSpace(lobby.LobbyName) ? BasisLocalization.Get("steam.lobby.defaultName", lobby.HostName) : lobby.LobbyName;
        }
        private static string Describe(BasisSteamLobbyState lobby)
        {
            string world = string.IsNullOrWhiteSpace(lobby.WorldName) ? BasisLocalization.Get("steam.lobbies.details.noWorld") : lobby.WorldName;
            string text = BasisLocalization.Get("steam.lobbies.details", lobby.HostName, world, lobby.MemberCount, lobby.MaxMembers);
            if (!lobby.IsCompatible)
            {
                text += "\n" + BasisLocalization.Get("steam.lobbies.details.incompatible", lobby.Version, BasisNetworkVersion.ServerVersion);
            }
            return text;
        }
    }
}
