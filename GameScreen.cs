using SpotifyAPI.Web;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using GOJ.Attributes;
using GOJ.Extensions;
using GOJ.Game;
using GOJ.Game.Gamemodes;
using GOJ.Networking;

namespace GOJ.UI.Screens
{
    public enum GameScreenOpeningCircumstance
    {
        RegularJoin,    //player was in the lobby from the start, normal setup
        LateJoin,       //first time join after the game has started
        Rejoin,         //left and rejoined the game
    }

    [ScreenName("GameScreen")]
    public class GameScreen : IVisualElement
    {
        public VisualElement Root { get; private set; }
        public bool IsInitialised { get; private set; }

        PlayedTrackInfoUI m_currentTrackInfoUI;

        RoundTimerUI m_roundTimer;
        ScoreboardUI m_scoreboard;

        Button m_settingsButton;

        PlayerToggles m_playerToggles;

        /// <summary>
        /// How many players have I voted for this round?
        /// </summary>
        int m_votedPlayerCount;

        Button m_submitVoteButton;
        Button m_continueButton;

        CopyStringToClipboardButton m_copyLobbyCodeButton;

        Gamemode m_currentGamemode;
        IGameScreenSetupStrategy m_setupStrategy;

        bool m_isFirstRoundAsLateJoiner;

        public void Initialise(VisualElement root, params object[] args)
        {
            Root = root;

            root.RegisterCallback<DetachFromPanelEvent>(Cleanup);

            m_currentGamemode = args[0] as Gamemode;
            VisualTreeAsset playerToggleTemplate = args[1] as VisualTreeAsset;
            SetupVisualElementReferences(root, m_currentGamemode, playerToggleTemplate);            

            Gamemode.OnNewRound += OnNewRoundStarted;
            Gamemode.OnCalculatedResults += DisplayResults;
            WhoseSongGamemode.OnVotesRequested += OnTimerExpired;

            InitialiseAddToPlaylistNotification(root);

            GameState currentGameState = (GameState)args[2];
            GameScreenOpeningCircumstance openingType = (GameScreenOpeningCircumstance)args[3];
            m_isFirstRoundAsLateJoiner = openingType == GameScreenOpeningCircumstance.LateJoin;

            m_setupStrategy = GetGameScreenSetupStrategy(currentGameState, openingType);
            m_setupStrategy.SetupGameScreen();

            FetchCurrentTrackIfJoinedLate(currentGameState);

            IsInitialised = true;
        }

        void FetchCurrentTrackIfJoinedLate(GameState gameStateOnOpening)
        {
            if (gameStateOnOpening == GameState.GameScreenGuessing)
            {
                //late joiners update their UI from the currently playing track
                WhoseSongGamemode.OnReceivedNewTrack += OnReceivedTrackAsLateJoiner;
                (m_currentGamemode as WhoseSongGamemode).RequestCurrentTrackServerRpc();
            }
            else
            {
                Logger.LogWarning("Don't need to fetch track info since we are already guessing");
            }
        }

        void OnReceivedTrackAsLateJoiner(FullTrack track)
        {
            WhoseSongGamemode.OnReceivedNewTrack -= OnReceivedTrackAsLateJoiner;
            (m_currentGamemode as WhoseSongGamemode).PlayerHasTrackServerRpc(TrackRetrievalManager.Instance.MyTracks.ContainsKey(track.Id));
            OnNewRoundStarted(track);

            if (m_isFirstRoundAsLateJoiner)
            {
                //Prevent being able to vote for myself as I'm new this round
                m_playerToggles.DisablePlayerToggle(GameManager.Instance.LocalPlayerId);
            }
        }

        public void Dispose()
        {
            WhoseSongGamemode.OnReceivedNewTrack -= OnReceivedTrackAsLateJoiner;
        }

        void SetupVisualElementReferences(VisualElement root, Gamemode currentGamemode, VisualTreeAsset playerToggleTemplate)
        {
            m_settingsButton = root.Q<Button>("SettingsButton");
            m_settingsButton.clicked += OpenSettings;

            m_currentTrackInfoUI = new PlayedTrackInfoUI(root.Q("CurrentTrack"));

            VisualElement timerElement = root.Q("RoundTimer");
            m_roundTimer = new RoundTimerUI(timerElement);

            if (m_currentGamemode is ITimedMode)
                RoundTimer.OnTimerUpdated += OnTimerUpdated;
            else
                timerElement.Hide(false);

            VisualElement scoreboardElement = root.Q("ScoreboardContainer");
            m_scoreboard = new ScoreboardUI(scoreboardElement);

            VisualElement playerTogglesContainer = root.Q("ToggleContainer");
            m_playerToggles = new PlayerToggles(playerToggleTemplate, playerTogglesContainer, OnPlayerToggled);

            m_submitVoteButton = root.Q<Button>("SubmitButton");
            m_submitVoteButton.clicked += OnSubmitVotes;
            m_submitVoteButton.SetEnabled(false);

            m_continueButton = root.Q<Button>("ContinueButton");
            m_continueButton.clicked += OnClickedContinue;
            m_continueButton.Hide();

            Label lobbyCodeLabel = root.Q<Label>("LobbyCode");
            lobbyCodeLabel.text = LobbyManager.Instance.JoinedLobby.LobbyCode;

            m_copyLobbyCodeButton = root.Q<CopyStringToClipboardButton>("CopyLobbyCodeButton");
            m_copyLobbyCodeButton.SetTextToCopy(LobbyManager.Instance.JoinedLobby.LobbyCode);
        }

        void InitialiseAddToPlaylistNotification(VisualElement root)
        {
            VisualElement addToPlaylistRoot = Root.Q("AddedToPlaylistNotification");

            AddToPlaylistQueueUI addToPlaylistQueueUI = new AddToPlaylistQueueUI(addToPlaylistRoot);
            AddToPlaylistQueue addedToPlaylistQueue = new AddToPlaylistQueue();

            addToPlaylistQueueUI.SubscribeToQueue(addedToPlaylistQueue);
            addedToPlaylistQueue.SubscribeToNotificationEndEvent(addToPlaylistQueueUI);
        }

        public void UpdateData(params object[] args)
        {
        }

        public void Reset()
        {
            throw new System.NotImplementedException();
        }

        public async void OnNewRoundStarted(FullTrack track)
        {
            //Might need some special setup depending on how we joined the game but after that it's back to regular round flow
            if (m_setupStrategy is IGameScreenNewRoundSetupStrategy newRoundSetup)
            {
                newRoundSetup.FirstNewRoundSetup();
                m_setupStrategy = null;
            }            

            m_scoreboard.OnNewRound(track);
            m_scoreboard.ClearResults();

            m_playerToggles.OnNewRoundStarted(track);
            ResetPlayerToggles();

            m_votedPlayerCount = 0;

            m_continueButton.Hide();

            m_submitVoteButton.text = "Submit Votes";
            m_submitVoteButton.Show();

            m_currentTrackInfoUI.UpdateTrackInfoUI(track);

            if (!GameManager.Instance.DebugDontPlaySong)
            {                
                await RequestPlaybackForTrack(track);
            }
            else
            {
                Logger.LogFormat("DEBUG NOT AFFECTING PLAYBACK, next song is\n{0}", track.Name);
            }
        }

        void ResetPlayerToggles()
        {
            m_playerToggles.ResetPlayerToggles();
        }

        async Task RequestPlaybackForTrack(FullTrack track)
        {
            if (!GameManager.Instance.IsSpotifyPremium)
            {
                Logger.LogWarning("User isn't premium member, not starting song playback");
                return;
            }

            PlayerResumePlaybackRequest request = new PlayerResumePlaybackRequest();
            request.Uris = new List<string>() { track.Uri };
            request.PositionMs = Mathf.FloorToInt(GameSessionManager.Instance.SpotifySessionSettings.Value.SongStartPercentage * track.DurationMs);

            await Helpers.SpotifyEndpointHelpers.StartResumePlayback(request);
        }

        void OnTimerUpdated(float timeRemaining)
        {
            m_roundTimer.UpdateTimer(timeRemaining, RoundTimer.MAX_GUESSING_TIME);
        }       

        void OnPlayerToggled(ChangeEvent<bool> evt)
        {
            if (evt.newValue)
            {
                m_votedPlayerCount++;
            }
            else
            {
                m_votedPlayerCount--;
            }

            m_submitVoteButton.SetEnabled(m_votedPlayerCount > 0);
        }

        void OnSubmitVotes()
        {
            if(m_votedPlayerCount == 0)            
                return;            

            m_submitVoteButton.SetEnabled(false);
            m_submitVoteButton.text = "Submitted";

            SendCurrentlyVotedPlayers();
        }

        void OnTimerExpired(Gamemode currentGamemode)
        {
            m_submitVoteButton.SetEnabled(false);
            SendCurrentlyVotedPlayers();
        }

        void SendCurrentlyVotedPlayers()
        {
            //gather player toggles and send their ids, along with current user's, to central game controller
            HashSet<string> votes = GatherVotes();

            byte[] voteBytes = Helpers.NetworkConversionHelpers.ObjectToBytes(votes);

            m_currentGamemode.ReceiveVotesServerRpc(voteBytes);
        }

        HashSet<string> GatherVotes()
        {
            //gather player toggles and send their ids, along with current user's, to central game controller
            HashSet<string> votes = new HashSet<string>(m_playerToggles.Count());
            foreach (var toggle in m_playerToggles)
            {
                if (toggle.Value.value)
                {
                    votes.Add(toggle.Key);
                }

                //might need to disable completely instead of just picking mode
                toggle.Value.pickingMode = PickingMode.Ignore;
            }

            return votes;
        }

        void DisplayResults(RoundResults roundResults)
        {
            m_playerToggles?.OnResultsUpdated(roundResults);
            m_scoreboard?.OnResultsUpdated(roundResults);

            m_submitVoteButton.Hide();

            m_continueButton.text = "Continue";
            m_continueButton.SetEnabled(true);
            m_continueButton.Show();
        }

        void OnClickedContinue()
        {
            //send message to game controller that player is ready to continue
            GameSessionManager.Instance.OnPlayerReadyForNextRoundServerRpc();
            m_continueButton.SetEnabled(false);
            m_continueButton.text = "Waiting for others";
        }

        void OpenSettings()
        {
            ScreenManager.Instance.OpenSettings();
        }

        public void Cleanup(DetachFromPanelEvent evt)
        {
            RoundTimer.OnTimerUpdated -= OnTimerUpdated;

            Gamemode.OnNewRound -= OnNewRoundStarted;
            Gamemode.OnCalculatedResults -= DisplayResults;
            WhoseSongGamemode.OnVotesRequested -= OnTimerExpired;


            if(m_setupStrategy != null && m_setupStrategy is IDisposable disposable) 
                disposable.Dispose();

            m_scoreboard?.Dispose();
            m_playerToggles?.Dispose();

            Root.UnregisterCallback<DetachFromPanelEvent>(Cleanup);
        }

        IGameScreenSetupStrategy GetGameScreenSetupStrategy(GameState currentGameState, GameScreenOpeningCircumstance openingType)
        {
            if (currentGameState == GameState.GameScreenWaitingToContinueNextRound)
                return new LateJoinerGameScreenSetupStrategy(m_playerToggles, currentGameState);

            if(openingType == GameScreenOpeningCircumstance.LateJoin)
            {
                return new LateJoinerGameScreenSetupStrategy(m_playerToggles, currentGameState);
            }
            else
            {
                return new RegularGameScreenSetupStrategy(m_playerToggles, m_scoreboard, currentGameState);
            }
        }
    }
}