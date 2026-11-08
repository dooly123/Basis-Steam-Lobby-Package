using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.Common;
using Basis.Scripts.Networking;
using Basis.Scripts.UI.UI_Panels;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.UI;

namespace Basis.Network.Steam
{
    public class SteamLobbiesProvider : BasisMenuActionProvider<BasisMainMenu>
    {
        public const string TitleKey = "steam.lobbies.title";
        private static readonly int[] memberChoices = { 4, 8, 16, 32, 64 };
        private readonly List<string> visibilityEntries = new List<string> { nameof(BasisSteamLobbyVisibility.FriendsOnly), nameof(BasisSteamLobbyVisibility.Public), nameof(BasisSteamLobbyVisibility.Private) };
        private readonly List<string> visibilityKeys = new List<string> { "steam.lobbies.visibility.friendsOnly", "steam.lobbies.visibility.public", "steam.lobbies.visibility.private" };
        private readonly List<BasisSteamLobbyState> lobbies = new List<BasisSteamLobbyState>();
        private readonly List<PanelElementDescriptor> lobbyCards = new List<PanelElementDescriptor>();
        private readonly List<PanelButton> joinButtons = new List<PanelButton>();
        private readonly List<Friend> friends = new List<Friend>();
        private readonly List<BasisDataStoreItemKeys.ItemKey> worlds = new List<BasisDataStoreItemKeys.ItemKey>();
        private BasisMenuPanel panel;
        private RectTransform listContainer;
        private PanelElementDescriptor statusGroup, sessionGroup, emptyState;
        private PanelDropdown friendDropdown;
        private PanelButton hostButton, refreshButton, overlayButton, inviteButton, leaveButton;
        private DialogBox<bool> hostDialog;
        private PanelTextField lobbyNameField;
        private PanelDropdown worldDropdown, visibilityDropdown, membersDropdown;
        private PanelButton dialogHostButton;
        private bool searching;
        [NoAutoStaticsCleanup] private static SteamLobbiesProvider instance;
        [RuntimeInitializeOnLoadMethod]
        public static void AddToMenu()
        {
            instance = new SteamLobbiesProvider();
            BasisMenuBase<BasisMainMenu>.AddProvider(instance);
            ServersProvider.HeaderActionBuilders.Remove(BuildServersButton);
            ServersProvider.HeaderActionBuilders.Add(BuildServersButton);
        }
        private static void BuildServersButton(RectTransform headerActions)
        {
            PanelButton button = HotbarButton(headerActions, AddressableAssets.Sprites.People, BasisLocalization.Get(TitleKey), BasisLocalization.Get("steam.lobbies.servers.tooltip"));
            button.OnClicked += () => instance?.RunAction();
        }
        private static PanelButton HotbarButton(RectTransform parent, string icon, string title, string tooltip)
        {
            PanelButton button = PanelButton.CreateNew(PanelButton.ButtonStyles.Hotbar, parent);
            button.SetIcon(icon);
            button.Descriptor.SetTitle(title);
            button.Descriptor.SetTooltip(tooltip);
            button.SetSize(new Vector2(150, 150));
            button.Layout.flexibleWidth = 0f;
            button.EnableIconHoverAnimation();
            return button;
        }
        private static RectTransform ActionRow(RectTransform parent, string name)
        {
            RectTransform row = PanelElementDescriptor.BuildActionRow(parent, name);
            if (row.TryGetComponent(out HorizontalLayoutGroup layout))
            {
                layout.childForceExpandWidth = false;
                layout.childAlignment = TextAnchor.MiddleLeft;
            }
            return row;
        }
        public override string Title => BasisLocalization.Get(TitleKey);
        public override string IconAddress => AddressableAssets.Sprites.People;
        public override int Order => 31;
        public override bool Hidden => true;
        public override void RunAction()
        {
            if (BasisMainMenu.ActiveMenuTitle == Title)
            {
                BasisMainMenu.CloseActivePanel();
                return;
            }
            panel = BasisMainMenu.CreateActiveMenu(BasisMenuPanel.PanelData.Standard(Title), BasisMenuPanel.PanelStyles.Page);
            BasisMainMenu.FindProvider(ServersProvider.TitleStatic)?.BoundButton?.BindActiveStateToAddressablesInstance(panel);
            panel.OnInstanceReleased += OnPanelClosed;
            PanelTabPage page = PanelTabPage.CreateVertical(panel.Descriptor.ContentParent);
            page.Descriptor.SetTitle(Title);
            page.Descriptor.SetIcon(AddressableAssets.Sprites.People);
            Build(page.Descriptor.ContentParent);
            BasisSteamLobbyService.LobbyChanged += OnLobbyChanged;
            BasisSteamSession.StatusChanged += SetStatus;
            BasisSteamSession.Failed += SetStatus;
            if (BasisSteamLobbyService.EnsureReady())
            {
                SetStatus(BasisLocalization.Get("steam.lobbies.status.ready", SteamClient.Name));
                _ = SearchAsync();
            }
            else
            {
                SetStatus(BasisSteamLobbyService.LastError);
            }
            ApplyState();
        }
        private void Build(RectTransform container)
        {
            RectTransform actions = ActionRow(container, "SteamLobbyActions");
            PanelButton backButton = HotbarButton(actions, AddressableAssets.Sprites.Servers, ServersProvider.TitleStatic, BasisLocalization.Get("steam.lobbies.back"));
            backButton.OnClicked += () => BasisMainMenu.OpenWithProvider(ServersProvider.TitleStatic);
            hostButton = HotbarButton(actions, AddressableAssets.Sprites.Add, BasisLocalization.Get("steam.lobbies.host.short"), BasisLocalization.Get("steam.lobbies.host.description"));
            hostButton.OnClicked += () => _ = ShowHostDialogAsync();
            refreshButton = HotbarButton(actions, AddressableAssets.Sprites.Reset, BasisLocalization.Get("steam.lobbies.browse.refresh"), BasisLocalization.Get("steam.lobbies.browse.description"));
            refreshButton.OnClicked += () => _ = SearchAsync();
            overlayButton = HotbarButton(actions, AddressableAssets.Sprites.Link, BasisLocalization.Get("steam.lobbies.session.invite"), BasisLocalization.Get("steam.lobbies.session.overlay"));
            overlayButton.OnClicked += OpenOverlay;
            leaveButton = HotbarButton(actions, AddressableAssets.Sprites.Exit, BasisLocalization.Get("steam.lobbies.session.leaveShort"), BasisLocalization.Get("steam.lobbies.session.leave"));
            leaveButton.OnClicked += () => _ = LeaveAsync();
            statusGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            statusGroup.SetTitle(BasisLocalization.Get("steam.lobbies.status.title"));
            sessionGroup = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            sessionGroup.SetIcon(AddressableAssets.GetSprite(AddressableAssets.Sprites.People));
            RectTransform sessionContent = sessionGroup.ContentParent;
            friendDropdown = PanelDropdown.CreateNewEntry(sessionContent);
            friendDropdown.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.session.friend"));
            inviteButton = PanelButton.CreateNew(sessionContent);
            inviteButton.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.session.sendInvite"));
            inviteButton.OnClicked += InviteSelectedFriend;
            listContainer = container;
            emptyState = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, container);
            emptyState.SetTitle(BasisLocalization.Get("steam.lobbies.browse.empty"));
            emptyState.SetDescription(BasisLocalization.Get("steam.lobbies.browse.emptyHint"));
            RebuildLobbyCards();
        }
        private void OnPanelClosed()
        {
            BasisSteamLobbyService.LobbyChanged -= OnLobbyChanged;
            BasisSteamSession.StatusChanged -= SetStatus;
            BasisSteamSession.Failed -= SetStatus;
            panel = null;
            listContainer = null;
            statusGroup = sessionGroup = emptyState = null;
            friendDropdown = null;
            hostButton = refreshButton = overlayButton = inviteButton = leaveButton = null;
            hostDialog = null;
            ClearDialogFields();
            lobbyCards.Clear();
            joinButtons.Clear();
        }
        private void ClearDialogFields()
        {
            lobbyNameField = null;
            worldDropdown = visibilityDropdown = membersDropdown = null;
            dialogHostButton = null;
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
        private async Task ShowHostDialogAsync()
        {
            if (!IsOpen || hostDialog != null)
            {
                return;
            }
            DialogBox<bool> dialog = DialogBox<bool>.Create(panel, new Vector2(1170, 760), BasisLocalization.Get("steam.lobbies.host.title"), BasisLocalization.Get("steam.lobbies.host.description"), AddressableAssets.Sprites.Add);
            if (dialog.Descriptor == null)
            {
                return;
            }
            hostDialog = dialog;
            PanelButton exitButton = PanelButton.CreateNew(PanelButton.ButtonStyles.ExitButton, dialog.Descriptor.Header);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 125);
            exitButton.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 50);
            exitButton.OnClicked += () => dialog.Cancel(false);
            RectTransform content = dialog.Descriptor.ContentParent;
            lobbyNameField = PanelTextField.CreateNewEntry(content);
            lobbyNameField.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.host.name"));
            lobbyNameField.SetValueWithoutNotify(SteamClient.IsValid ? SteamClient.Name : string.Empty);
            worldDropdown = PanelDropdown.CreateNewEntry(content);
            worldDropdown.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.host.world"));
            FillWorlds();
            _ = LoadWorldsAsync();
            visibilityDropdown = PanelDropdown.CreateNewEntry(content);
            visibilityDropdown.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.host.visibility"));
            visibilityDropdown.AssignLocalizedEntries(visibilityEntries, visibilityKeys);
            visibilityDropdown.SetValueWithoutNotify(visibilityEntries[0]);
            membersDropdown = PanelDropdown.CreateNewEntry(content);
            membersDropdown.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.host.members"));
            List<string> members = new List<string>(memberChoices.Length);
            for (int index = 0; index < memberChoices.Length; index++)
            {
                members.Add(memberChoices[index].ToString(CultureInfo.InvariantCulture));
            }
            membersDropdown.AssignEntries(members);
            membersDropdown.SetValueWithoutNotify(BasisSteamSession.DefaultMaxMembers.ToString(CultureInfo.InvariantCulture));
            PanelTabGroup actionRow = PanelTabGroup.CreateNew(content, LayoutDirection.HorizontalNoBackground);
            actionRow.Descriptor.SetHeight(60);
            dialogHostButton = PanelButton.CreateNew(PanelButton.ButtonStyles.AcceptButton, actionRow.TabButtonParent);
            dialogHostButton.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.host.button"));
            dialogHostButton.Descriptor.SetWidth(200);
            dialogHostButton.Descriptor.SetHeight(60);
            dialogHostButton.SetInteractable(!BasisSteamSession.Busy && BasisSteamClient.IsReady);
            dialogHostButton.OnClicked += () =>
            {
                if (dialog.IsBusy)
                {
                    return;
                }
                BasisSteamHostRequest request = BuildHostRequest();
                dialog.IsBusy = true;
                dialog.CloseWithResult(true);
                _ = HostAsync(request);
            };
            PanelButton cancelButton = PanelButton.CreateNew(PanelButton.ButtonStyles.CancelButton, actionRow.TabButtonParent);
            cancelButton.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.host.cancel"));
            cancelButton.Descriptor.SetWidth(200);
            cancelButton.Descriptor.SetHeight(60);
            cancelButton.OnClicked += () =>
            {
                if (dialog.IsBusy)
                {
                    return;
                }
                dialog.IsBusy = true;
                dialog.CloseWithResult(false);
            };
            dialog.Descriptor.ForceRebuild();
            await dialog.WaitAsync();
            if (hostDialog == dialog)
            {
                hostDialog = null;
                ClearDialogFields();
            }
        }
        private BasisSteamHostRequest BuildHostRequest()
        {
            BasisDataStoreItemKeys.ItemKey world = SelectedWorld();
            return new BasisSteamHostRequest
            {
                LobbyName = Text(lobbyNameField),
                WorldUrl = world?.Url ?? string.Empty,
                WorldPassword = world?.Pass ?? string.Empty,
                Visibility = Enum.TryParse(visibilityDropdown.Value, out BasisSteamLobbyVisibility visibility) ? visibility : BasisSteamLobbyVisibility.FriendsOnly,
                MaxMembers = int.TryParse(membersDropdown.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxMembers) ? maxMembers : BasisSteamSession.DefaultMaxMembers,
            };
        }
        private async Task HostAsync(BasisSteamHostRequest request)
        {
            hostButton?.SetInteractable(false);
            try
            {
                await BasisSteamSession.HostAsync(request, null);
            }
            finally
            {
                if (IsOpen)
                {
                    ApplyState();
                }
            }
        }
        private async Task JoinAsync(BasisSteamLobbyState lobby)
        {
            if (lobby == null)
            {
                return;
            }
            SetJoinInteractable(false);
            try
            {
                await BasisSteamSession.JoinAsync(lobby.LobbyId, null);
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
            leaveButton?.SetInteractable(false);
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
            emptyState?.SetTitle(BasisLocalization.Get("steam.lobbies.browse.searching"));
            try
            {
                List<BasisSteamLobbyState> found = await BasisSteamLobbyService.SearchAsync();
                lobbies.Clear();
                ulong self = SteamClient.IsValid ? SteamClient.SteamId.Value : 0;
                for (int index = 0; index < found.Count; index++)
                {
                    if (found[index].HostSteamId != self)
                    {
                        lobbies.Add(found[index]);
                    }
                }
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
                emptyState?.SetTitle(BasisLocalization.Get("steam.lobbies.browse.empty"));
                RebuildLobbyCards();
                ApplyState();
            }
        }
        private void RebuildLobbyCards()
        {
            for (int index = 0; index < lobbyCards.Count; index++)
            {
                if (lobbyCards[index] != null)
                {
                    UnityEngine.Object.Destroy(lobbyCards[index].gameObject);
                }
            }
            lobbyCards.Clear();
            joinButtons.Clear();
            if (listContainer == null)
            {
                return;
            }
            for (int index = 0; index < lobbies.Count; index++)
            {
                BasisSteamLobbyState lobby = lobbies[index];
                PanelElementDescriptor card = PanelElementDescriptor.CreateNew(PanelElementDescriptor.ElementStyles.Group, listContainer);
                if (card == null)
                {
                    continue;
                }
                card.SetIcon(AddressableAssets.GetSprite(AddressableAssets.Sprites.World));
                card.SetTitle(BasisLocalization.Get("steam.lobbies.browse.entry", DisplayName(lobby), lobby.MemberCount, lobby.MaxMembers));
                card.SetDescription(Describe(lobby));
                RectTransform row = PanelElementDescriptor.BuildActionRow(card.ContentParent, "SteamLobbyCardActions");
                PanelButton join = PanelButton.CreateNew(row);
                join.Descriptor.SetTitle(BasisLocalization.Get("steam.lobbies.browse.join"));
                join.OnClicked += () => _ = JoinAsync(lobby);
                lobbyCards.Add(card);
                joinButtons.Add(join);
            }
            emptyState?.rectTransform.SetAsLastSibling();
        }
        private void SetJoinInteractable(bool interactable)
        {
            for (int index = 0; index < joinButtons.Count && index < lobbies.Count; index++)
            {
                joinButtons[index]?.SetInteractable(interactable && lobbies[index].IsCompatible);
            }
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
            bool ready = BasisSteamClient.IsReady;
            hostButton.SetInteractable(!busy && ready && !inLobby);
            refreshButton.SetInteractable(!searching && ready);
            overlayButton.SetInteractable(inLobby);
            leaveButton.SetInteractable(inLobby);
            dialogHostButton?.SetInteractable(!busy && ready);
            sessionGroup.SetActive(inLobby);
            if (inLobby)
            {
                sessionGroup.SetTitle(BasisLocalization.Get("steam.lobbies.session.titleNamed", DisplayName(current)));
                sessionGroup.SetDescription(Describe(current));
                FillFriends();
            }
            SetJoinInteractable(!busy);
            emptyState.SetActive(lobbies.Count == 0);
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
        private async Task LoadWorldsAsync()
        {
            try
            {
                await BasisDataStoreItemKeys.EnsureLoaded();
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"Steam lobby could not read the library for worlds: {ex.Message}", BasisDebug.LogTag.Networking);
                return;
            }
            if (worldDropdown != null)
            {
                FillWorlds();
            }
        }
        private void FillWorlds()
        {
            string previous = worldDropdown.Value;
            worlds.Clear();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BasisDataStoreItemKeys.ItemKey[] keys = BasisDataStoreItemKeys.DisplayKeys();
            for (int index = 0; index < keys.Length; index++)
            {
                BasisDataStoreItemKeys.ItemKey key = keys[index];
                if (key == null || key.Mode != BundledContentHolder.Mode.World || key.EmbeddedSettings.IsEmbedded || !IsRemote(key.Url) || !seen.Add(key.Url))
                {
                    continue;
                }
                worlds.Add(key);
            }
            List<string> entries = new List<string>(worlds.Count + 1) { string.Empty };
            List<string> labels = new List<string>(worlds.Count + 1) { BasisLocalization.Get("steam.lobbies.host.world.none") };
            for (int index = 0; index < worlds.Count; index++)
            {
                entries.Add(worlds[index].Url);
                labels.Add(WorldName(worlds[index]));
            }
            worldDropdown.AssignEntries(entries, labels);
            worldDropdown.SetValueWithoutNotify(entries.Contains(previous) ? previous : entries[0]);
        }
        private BasisDataStoreItemKeys.ItemKey SelectedWorld()
        {
            string url = worldDropdown?.Value;
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }
            for (int index = 0; index < worlds.Count; index++)
            {
                if (string.Equals(worlds[index].Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    return worlds[index];
                }
            }
            return null;
        }
        private static bool IsRemote(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
        private static string WorldName(BasisDataStoreItemKeys.ItemKey key)
        {
            if (CachedMetaData.TryGetMeta(key.Url, out CachedMetaData.CachedContent meta) && !string.IsNullOrWhiteSpace(meta.Name))
            {
                return meta.Name;
            }
            Uri uri = new Uri(key.Url);
            string last = uri.Segments.Length > 0 ? Uri.UnescapeDataString(uri.Segments[uri.Segments.Length - 1]) : string.Empty;
            return string.IsNullOrWhiteSpace(last) ? key.Url : last;
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
