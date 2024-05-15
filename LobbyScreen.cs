using SpotifyAPI.Web;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using GOJ.Attributes;
using GOJ.Extensions;
using GOJ.Game;
using GOJ.Networking;

namespace GOJ.UI.Screens
{
    [ScreenName("LobbyScreen")]
    public class LobbyScreen : IVisualElement
    {        
        public VisualElement Root { get; private set; }
        public bool IsInitialised { get; private set; }

        Dictionary<int, PersonalizationTopRequest.TimeRange> m_timeRangeMap = new Dictionary<int, PersonalizationTopRequest.TimeRange>()
        {
            {0, PersonalizationTopRequest.TimeRange.ShortTerm },
            {1, PersonalizationTopRequest.TimeRange.MediumTerm },
            {2, PersonalizationTopRequest.TimeRange.LongTerm }
        };
        
        //Displayed to everyone
        Label m_lobbyCode;
        CopyStringToClipboardButton m_copyCodeButton;
        Label m_playerCount;
        VisualElement m_playerNamesParent;
        Dictionary<string, Label> m_playerNames = new Dictionary<string, Label>();

        Button m_leaveLobbyButton;

        #region Host Only
        SpotifySettings m_settings;
        
        VisualElement m_hostOptionsParent;
        Slider m_songStartPercentageSlider;
        Label m_percentageLabel;

        Toggle m_useTopSongsToggle;
        Toggle m_useLikedSongsToggle;
        Toggle m_useSongsInPlaylistToggle;        

        RadioButtonGroup m_timeRangeOptions;

        Button m_startGameButton;
        bool m_startingGame = false;
        #endregion

        GameObject m_gameSessionManagerPrefab;

        public void Initialise(VisualElement root, params object[] args)
        {
            Root = root;

            root.RegisterCallback<DetachFromPanelEvent>(Cleanup);

            InitialisePlayerObjects();

            bool isHost = AuthenticationService.Instance.PlayerId == LobbyManager.Instance.JoinedLobby.HostId;

            if (isHost)
                m_settings = CreateSessionSettings();

            InitialiseHostObjects();

            if (!isHost)
            {
                LobbyManager.Instance.OnRelayConnectionCreated += ConnectToRelay;
            }

            m_gameSessionManagerPrefab = args[0] as GameObject;

            IsInitialised = true;
        }

        public void UpdateData(params object[] args)
        {
            m_lobbyCode.text = string.Format("Lobby Code: {0}", LobbyManager.Instance.JoinedLobby.LobbyCode);

            bool isHost = AuthenticationService.Instance.PlayerId == LobbyManager.Instance.JoinedLobby.HostId;
            UpdateHostObjects(isHost);

            UpdatePlayerNames();
        }

        void UpdateHostObjects(bool isHost)
        {
            if (isHost)
            {
                m_useTopSongsToggle.value = m_settings.UseTopSongs;
                m_useLikedSongsToggle.value = m_settings.UseLikedSongs;
                m_useSongsInPlaylistToggle.value = m_settings.UseSongsInPlaylists;

                m_songStartPercentageSlider.value = m_settings.SongStartPercentage;
            }
            m_hostOptionsParent.SetVisibility(isHost);
            m_startGameButton.SetVisibility(isHost);
        }

        SpotifySettings CreateSessionSettings()
        {
            //set up defaults for the session settings
            SpotifySettings sessionSettings = new SpotifySettings();
            
            //The enum order here is reverse to what the UI shows so using medium term as it's in the middle for both
            sessionSettings.SelectedTopSongsTimeRange = PersonalizationTopRequest.TimeRange.MediumTerm; 
            sessionSettings.UseRecentlyPlayed = false;
            sessionSettings.UseLikedSongs = true;
            sessionSettings.UseTopSongs = true;
            sessionSettings.UseSongsInPlaylists = true;
            sessionSettings.SongStartPercentage = 0;

            return sessionSettings;
        }

        void UpdatePlayerNames()
        {
            UpdatePlayerCount(LobbyManager.Instance.JoinedLobby.Players.Count);

            m_playerNames.Clear();
            m_playerNamesParent.Clear();

            foreach (var player in LobbyManager.Instance.JoinedLobby.Players)
            {
                AddPlayerName(player.Id, player.Data["PlayerName"].Value);
            }
        }

        void AddPlayerName(string playerId, string playerName)
        {
            if(!m_playerNames.ContainsKey(playerId))
            {
                Label label = new Label(playerName);
                //TODO make host display an icon (crown?) instead of asterisks
                if(LobbyManager.Instance.JoinedLobby.HostId == playerId)
                {
                    label.text = string.Format("** {0} **", playerName);
                }
                label.AddToClassList("lobby-player-name");                

                m_playerNames.Add(playerId, label);
                m_playerNamesParent.Add(label);
            }
            else
            {
                Logger.LogFormat("Already have player {0}", playerName);
            }
        }

        public void Reset()
        {
            throw new System.NotImplementedException();
        }

        void InitialisePlayerObjects()
        {
            m_lobbyCode = Root.Q<Label>("LobbyCode");

            m_copyCodeButton = Root.Q<CopyStringToClipboardButton>("CopyLobbyCodeButton");
            m_copyCodeButton.SetTextToCopy(LobbyManager.Instance.JoinedLobby.LobbyCode);

            m_playerCount = Root.Q<Label>("PlayerCount");          

            m_playerNamesParent = Root.Q("PlayerInfos");

            LobbyManager.Instance.OnLobbyPlayersChanged += UpdatePlayerNames;
            LobbyManager.Instance.OnLobbyHostChanged += OnHostChanged;

            m_leaveLobbyButton = Root.Q<Button>("LeaveButton");
            m_leaveLobbyButton.clicked += LeaveLobby;
        }

        void InitialiseHostObjects()
        {
            m_hostOptionsParent = Root.Q("LobbySettings");

            //Host only
            m_percentageLabel = Root.Q<Label>("PercentageLabel");

            m_songStartPercentageSlider = Root.Q<Slider>("SongStartSlider");
            m_songStartPercentageSlider.RegisterValueChangedCallback(OnSongStartPercentageUpdated);

            Button beginningButton = Root.Q<Button>("BeginningButton");
            beginningButton.clicked += () => { m_songStartPercentageSlider.value = 0; };

            Button middleButton = Root.Q<Button>("MiddleButton");
            middleButton.clicked += () => { m_songStartPercentageSlider.value = 0.5f; };

            Button endbutton = Root.Q<Button>("EndButton");
            endbutton.clicked += () => { m_songStartPercentageSlider.value = 0.9f; };

            m_useLikedSongsToggle= Root.Q<Toggle>("LikedSongsToggle");
            m_useLikedSongsToggle.RegisterValueChangedCallback(OnUseLikedSongsToggled);

            m_useSongsInPlaylistToggle = Root.Q<Toggle>("SongsOnPlaylistToggle");
            m_useSongsInPlaylistToggle.RegisterValueChangedCallback(OnUseSongsInPlaylistsToggled);

            m_timeRangeOptions = Root.Q<RadioButtonGroup>("TimeRangeOptions");
            m_timeRangeOptions.SetValueWithoutNotify((int)m_settings.SelectedTopSongsTimeRange);
            m_timeRangeOptions.RegisterValueChangedCallback(OnTimeRangeUpdated);

            m_useTopSongsToggle = Root.Q<Toggle>("TopPlayedSongsToggle");
            m_useTopSongsToggle.RegisterValueChangedCallback(OnUseTopSongsToggled);

            m_startGameButton = Root.Q<Button>("StartButton");
            m_startGameButton.clicked += StartGame;
        }

        void OnSongStartPercentageUpdated(ChangeEvent<float> evt)
        {
            m_settings.SongStartPercentage = Mathf.Clamp01(evt.newValue);
            m_percentageLabel.text = FormatPercentageString(evt.newValue);
        }

        void OnUseTopSongsToggled(ChangeEvent<bool> evt)
        {
            m_settings.UseTopSongs = evt.newValue;
            m_timeRangeOptions.SetVisibility(evt.newValue, false);

            ValidateStartButton();
        }

        void OnTimeRangeUpdated(ChangeEvent<int> evt)
        {
            m_settings.SelectedTopSongsTimeRange = m_timeRangeMap[evt.newValue];
            Logger.LogFormat("Changed time range to {0}", m_settings.SelectedTopSongsTimeRange);
        }

        void OnUseLikedSongsToggled(ChangeEvent<bool> evt)
        {
            m_settings.UseLikedSongs = evt.newValue;

            ValidateStartButton();
        }

        void OnUseSongsInPlaylistsToggled(ChangeEvent<bool> evt)
        {
            m_settings.UseSongsInPlaylists = evt.newValue;

            ValidateStartButton();
        }

        void ValidateStartButton()
        {
            bool enabled = m_settings.UseLikedSongs || m_settings.UseTopSongs || m_settings.UseSongsInPlaylists;

            m_startGameButton.SetEnabled(enabled);
        }

        void OnHostChanged(string newHostId)
        {
            bool isHost = AuthenticationService.Instance.PlayerId == LobbyManager.Instance.JoinedLobby.HostId;

            if (!isHost)
                return;

            UpdateHostObjects(isHost);
        }

        async void StartGame()
        {
            if (m_startingGame)
                return;

            m_startingGame = true;

            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnServerStopped += OnServerStopped;

            var allocation = await Helpers.NetworkHelpers.CreateRelayAsHost(LobbyManager.Instance.JoinedLobby);

            var gameSessionManager = Helpers.NetworkObjectHelpers.SpawnNetworkObject<GameSessionManager>(m_gameSessionManagerPrefab, false);

            GameSessionManager.Instance.SpotifySessionSettings.Value = m_settings;

            //manually spawn the host's player object rather than automatic from network manager
            GameSessionManager.Instance.SpawnPlayer(NetworkManager.Singleton.LocalClientId);

            NetworkManager.Singleton.SceneManager.LoadScene("WhoseSongIsItScene", LoadSceneMode.Single);

            await Helpers.NetworkHelpers.SetLobbyAllocationId(LobbyManager.Instance.JoinedLobby, allocation);
        }

        void OnClientConnected(ulong clientId)
        {
            if (clientId == NetworkManager.Singleton.LocalClientId)
                return;

            GameSessionManager.Instance.SpawnPlayer(clientId);
        }

        void OnServerStopped(bool stopped)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
        }

        async void ConnectToRelay(string relayJoinCode)
        {
            await Helpers.NetworkHelpers.JoinRelay(relayJoinCode, LobbyManager.Instance.JoinedLobby, AuthenticationService.Instance.PlayerId);
        }

        async void LeaveLobby()
        {
            //tell server i'm leaving
            //if i was host, close the lobby or try to transfer host
            //change back to menu screen
            try
            {
                await LobbyManager.Instance.LeaveLobby();
            }
            catch (LobbyServiceException ex)
            {
                Logger.LogErrorFormat("Error leaving lobby: {0}", ex.Message);
            }
        }

        void UpdatePlayerCount(int count)
        {
            m_playerCount.text = string.Format("Players: {0}/{1}", count, LobbyManager.Instance.JoinedLobby.MaxPlayers);
        }

        string FormatPercentageString(float percentage)
        {
            return string.Format("{0}%", Mathf.FloorToInt(percentage * 100));
        }

        public void Cleanup(DetachFromPanelEvent evt)
        {
            LobbyManager.Instance.OnRelayConnectionCreated -= ConnectToRelay; 
            LobbyManager.Instance.OnLobbyPlayersChanged -= UpdatePlayerNames;
            LobbyManager.Instance.OnLobbyHostChanged -= OnHostChanged;

            Root.UnregisterCallback<DetachFromPanelEvent>(Cleanup);
        }
    }
}