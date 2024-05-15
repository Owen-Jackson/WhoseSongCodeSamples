using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using GOJ.Game.Gamemodes;
using GOJ.Networking;
using System.Linq;

namespace GOJ.Game
{
    public class GameSessionManager : NetworkBehaviour
    {
        public static GameSessionManager Instance { get; private set; }

        //load this and add the gamemode as a component before spawning
        [SerializeField]
        NetworkObject m_whosSongGamemodePrefab;

        NetworkObject m_spawnedGamemodeObject;

        public NetworkVariable<ulong> TrackRetrievalNetworkObjectId { get; set; } = new NetworkVariable<ulong>();

        [SerializeField]
        public NetworkVariable<NetworkObjectReference> CurrentGamemodeObject { get; set; } = new NetworkVariable<NetworkObjectReference>();
        public Gamemode CurrentGamemode { get; private set; }

        [SerializeField]
        PlayerColours m_playerColours;
        public PlayerColours PlayerColours { get { return m_playerColours; } }

        [SerializeField]
        NetworkObjectSpawner m_playerObjectSpawner;

        public Dictionary<ulong, string> ClientIdToPlayerId { get; private set; } = new Dictionary<ulong, string>();
        public Dictionary<string, PlayerSessionData> PlayerData { get; private set; } = new Dictionary<string, PlayerSessionData>();

        public NetworkList<ActivePlayerData> ActivePlayers { get; private set; }
        public NetworkList<NetworkBehaviourReference> PlayersWaitingToJoin { get; private set; }

        /// <summary>
        /// Contains player ids of anyone who shouldn't be selectable this round, includes disconnected players and
        /// rejoining players that missed the round's start
        /// </summary>
        public NetworkList<FixedString512Bytes> PlayersNotSelectableThisRoundIds { get; private set; }

        [SerializeField]
        private NetworkVariable<SpotifySettings> m_spotifySessionSettings = new NetworkVariable<SpotifySettings>();
        public NetworkVariable<SpotifySettings> SpotifySessionSettings
        {
            get { return m_spotifySessionSettings; }
            set { m_spotifySessionSettings = value; }
        }

        public NetworkVariable<bool> SessionHasStarted { get; private set; } = new NetworkVariable<bool>(false);

        NetworkVariable<EndGameStateArgs> m_currentGameFinalResults = new NetworkVariable<EndGameStateArgs>();
        public NetworkVariable<EndGameStateArgs> CurrentGameFinalResults => m_currentGameFinalResults;

        List<ulong> m_playersReadyToContinueClientIds = new List<ulong>(Helpers.NetworkHelpers.MAX_PLAYERS);
        List<ulong> m_playersReadyToRestartClientIds = new List<ulong>(Helpers.NetworkHelpers.MAX_PLAYERS);

        NetworkVariable<GameState> m_gameState = new NetworkVariable<GameState>(GameState.GameScreenLoadingEveryoneIn);
        public GameState GameState => m_gameState.Value;

        private void Awake()
        {
            ActivePlayers = new NetworkList<ActivePlayerData>();
            PlayersWaitingToJoin = new NetworkList<NetworkBehaviourReference>();
            PlayersNotSelectableThisRoundIds = new NetworkList<FixedString512Bytes>();
        }

        public override void OnNetworkSpawn()
        {            
            if (IsServer && Instance != null)
            {
                NetworkObject.Despawn();
            }

            Instance = this;
            DontDestroyOnLoad(this.gameObject);

            if (!IsServer)
                return;
            
            m_playerColours.Reinitialise();

            PlayerObject.OnNetworkDespawned += OnPlayerDespawned;

            CurrentGamemodeObject.OnValueChanged += OnGamemodeObjectChanged;

            Gamemode.OnCalculatedResults += OnRoundEnded;
        }

        public override void OnNetworkDespawn()
        {
            Instance = null;

            if (!IsServer)
                return;

            CurrentGamemodeObject.OnValueChanged -= OnGamemodeObjectChanged;

            PlayerObject.OnNetworkDespawned -= OnPlayerDespawned;

            Gamemode.OnCalculatedResults -= OnRoundEnded;

            OnServerEnded();
        }

        #region Connection Management  

        void OnPlayerDespawned(NetworkBehaviourReference player, ulong clientId)
        {
            //Host left so don't need to bother with the rest
            if (clientId == NetworkManager.ServerClientId)
                return;

            if (ClientIdToPlayerId.ContainsKey(clientId))
            {
                string despawnedPlayerId = ClientIdToPlayerId[clientId];

                //have to manually loop through as NetworkList doesn't support predicates or LINQ
                foreach (var playerData in ActivePlayers)
                {
                    if (playerData.PlayerId.ToString() == despawnedPlayerId)
                    {
                        ActivePlayers.Remove(playerData);
                    }
                    else if (PlayersWaitingToJoin.Contains(player))
                    {
                        PlayersWaitingToJoin.Remove(player);
                    }
                }
            }

            switch (m_gameState.Value)
            {
                case GameState.GameScreenGuessing:
                    if(CurrentGamemode != null)                        
                        CurrentGamemode.PlayerDisconnected(clientId);
                    break;

                case GameState.GameScreenWaitingToContinueNextRound:
                    m_playersReadyToContinueClientIds.Remove(clientId);
                    EndRoundIfEveryoneReady();
                    break;

                case GameState.EndScreen:
                    m_playersReadyToRestartClientIds.Remove(clientId);
                    if(clientId != NetworkManager.LocalClientId)
                        StartNewGameIfEveryoneReady();
                    break;
            }
        }

        void MoveWaitingToJoinPlayersToActive()
        {
            if (PlayersWaitingToJoin.Count == 0)
                return;            

            foreach (var player in PlayersWaitingToJoin)
            {
                if (player.TryGet(out PlayerObject playerObject))
                    ActivePlayers.Add(new ActivePlayerData(playerObject.PlayerId, playerObject, true));
                else
                    Logger.LogError("Error getting player object from waiting to join player");
            }

            PlayersWaitingToJoin.Clear();
        }

        public void DisconnectClient(ulong clientId)
        {
            if (SessionHasStarted.Value)
            {
                if (ClientIdToPlayerId.TryGetValue(clientId, out var playerId))
                {
                    if (GetPlayerData(playerId).ClientId == clientId)
                    {
                        //save client data so they can reconnect
                        var clientData = PlayerData[playerId];
                        clientData.IsConnected = false;
                        PlayerData[playerId] = clientData;                        
                    }
                }
            }
            else
            {
                //session not started so no need to save data
                if (ClientIdToPlayerId.TryGetValue(clientId, out var playerId))
                {
                    ClientIdToPlayerId.Remove(clientId);
                    if (GetPlayerData(playerId).ClientId == clientId)
                    {
                        PlayerData.Remove(playerId);
                    }
                }
            }
        }

        bool IsDuplicateConnection(string playerId)
        {
            return PlayerData.ContainsKey(playerId) && PlayerData[playerId].IsConnected;
        }

        public void SetupConnectingPlayerSessionData(ulong clientId, string playerId, PlayerSessionData playerSessionData, PlayerObject playerObject)
        {
            bool isReconnecting = false;

            if (IsDuplicateConnection(playerId))
            {
                Logger.LogErrorFormat($"Player id {playerId} already exists. This is a duplicate connection. Rejecting this session data");
                return;
            }

            if (PlayerData.ContainsKey(playerId))
            {
                if (!PlayerData[playerId].IsConnected)
                {
                    isReconnecting = true;
                }
            }

            if (isReconnecting)
            {
                playerSessionData = PlayerData[playerId];
                playerSessionData.ClientId = clientId;
                playerSessionData.IsConnected = true;
                playerSessionData.PlayerName = playerObject.UserName;
                playerSessionData.PlayExplicitSongs = playerObject.PlayExplicitSongs;
                PlayersNotSelectableThisRoundIds.Remove(playerId);
            }
            else
            {
                Logger.Log($"Adding new player data: {playerSessionData}");
            }

            ClientIdToPlayerId[clientId] = playerId;
            PlayerData[playerId] = playerSessionData;

            AddSpawnedPlayerToListOfPlayers(playerObject, playerSessionData.IsInThisRound);
        }

        void AddSpawnedPlayerToListOfPlayers(PlayerObject player, bool isInThisRound)
        {
            if (!IsHost)
                return;

            string playerId = player.PlayerId;

            if (PlayerData.ContainsKey(playerId))
            {
                ActivePlayers.Add(new ActivePlayerData(player.PlayerId, player, isInThisRound));
            }
            else if (GameState != GameState.EndScreen)
            {
                PlayersWaitingToJoin.Add(player);
            }
        }

        public void SpawnPlayer(ulong clientId)
        {
            m_playerObjectSpawner.SpawnObject(clientId);
        }

        public void OnServerEnded()
        {
            PlayerData.Clear();
            ClientIdToPlayerId.Clear();
            ActivePlayers.Clear();
            PlayersWaitingToJoin.Clear();

            SessionHasStarted.Value = false;
        }

        void ClearDisconnectedPlayersData()
        {
            List<ulong> idsToClear = new List<ulong>();

            foreach (var clientId in ClientIdToPlayerId.Keys)
            {
                var data = GetPlayerData(clientId);
                if (!data.IsConnected)
                {
                    idsToClear.Add(clientId);
                }
            }

            foreach (var clientId in idsToClear)
            {
                string playerId = ClientIdToPlayerId[clientId];
                if (GetPlayerData(playerId).ClientId == clientId)
                {
                    PlayerData.Remove(playerId);
                }

                ClientIdToPlayerId.Remove(clientId);
            }

            PlayersNotSelectableThisRoundIds.Clear();
        }

        public PlayerSessionData GetPlayerData(ulong clientId)
        {
            string playerId = ClientIdToPlayerId[clientId];
            if (playerId != null)
                return GetPlayerData(playerId);

            return default;
        }

        public PlayerSessionData GetPlayerData(string playerId)
        {
            if (!PlayerData.TryGetValue(playerId, out PlayerSessionData result))
            {
                Logger.LogErrorFormat($"Error getting player session data for {playerId}");
            }

            return result;
        }

        #endregion

        /// <summary>
        /// <para>Currently this only sets to use the Who's Song gamemode.</para>
        /// <para>TODO update this when more gamemodes get added</para>
        /// </summary>
        public void SetGamemode(Gamemode gamemode)
        {
            if (IsHost)
            {
                CurrentGamemodeObject.Value = gamemode.NetworkObject;
            }
        }

        void OnGamemodeObjectChanged(NetworkObjectReference oldValue, NetworkObjectReference newValue)
        {
            if (newValue.TryGet(out var networkObj))
            {
                CurrentGamemode = networkObj.GetComponent<Gamemode>();
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void OnPlayerReadyForNextRoundServerRpc(ServerRpcParams serverRpcParams = default)
        {
            if(m_gameState.Value == GameState.GameScreenGuessing)            
                return;            

            m_playersReadyToContinueClientIds.Add(serverRpcParams.Receive.SenderClientId);

            EndRoundIfEveryoneReady();
        }

        void EndRoundIfEveryoneReady()
        {
            Logger.Log($"Ending round if everyone ready, ready players: {m_playersReadyToContinueClientIds.Count} / {ActivePlayers.Count}");            

            if (m_playersReadyToContinueClientIds.Count >= ActivePlayers.Count)
            {
                EndRound();
            }
        }

        void EndRound()
        {
            m_playersReadyToContinueClientIds.Clear();

            MoveWaitingToJoinPlayersToActive();

            //set whether each player is in from the start of this round, if someone leaves from here on they will be reconnected like normal
            foreach (var playerId in PlayerData.Keys.ToList())
            {
                PlayerSessionData temp = PlayerData[playerId];
                temp.IsInThisRound = temp.IsConnected;
                PlayerData[playerId] = temp;

                if (!temp.IsConnected && !PlayersNotSelectableThisRoundIds.Contains(playerId))
                {
                    PlayersNotSelectableThisRoundIds.Add(playerId);
                }
            }

            string winnerPlayerId = CurrentGamemode.CheckIfPlayerHasWon();
            if (!string.IsNullOrEmpty(winnerPlayerId))
            {
                OpenEndGameScreen(winnerPlayerId);
            }
            else
            {
                StartNextRound();
            }
        }

        void OpenEndGameScreen(string winnerPlayerId)
        {
            PlayerResultNetworkData[] playerResults = new PlayerResultNetworkData[CurrentGamemode.PlayerScores.Count];
            int count = 0;
            foreach (var player in CurrentGamemode.PlayerScores)
            {
                playerResults[count++] = new PlayerResultNetworkData(PlayerData[player.Key].PlayerName, player.Value);
            }

            m_currentGameFinalResults.Value = new EndGameStateArgs(winnerPlayerId, playerResults);

            m_gameState.Value = GameState.EndScreen;

            //open winner screen            
            if(NetworkManager.Singleton != null && NetworkManager.Singleton.SceneManager != null)
                NetworkManager.Singleton.SceneManager.LoadScene("EndGameScene", UnityEngine.SceneManagement.LoadSceneMode.Single);            
        }

        public void StartNextRound()
        {
            m_playersReadyToContinueClientIds.Clear();

            SessionHasStarted.Value = true;

            m_gameState.Value = GameState.GameScreenGuessing;

            CurrentGamemode.StartNextRound();
        }

        [ServerRpc(RequireOwnership = false)]
        public void OnPlayerReadyForNewGameServerRpc(ServerRpcParams serverRpcParams = default)
        {
            m_playersReadyToRestartClientIds.Add(serverRpcParams.Receive.SenderClientId);

            StartNewGameIfEveryoneReady();
        }

        void StartNewGameIfEveryoneReady()
        {
            if(NetworkManager.Singleton == null || NetworkManager.Singleton.SceneManager == null)
            {
                Logger.LogWarning("Network manager or network scene manager is null, cannot start new game");
                return;
            }

            if (m_playersReadyToRestartClientIds.Count >= ActivePlayers.Count)
            {
                m_playersReadyToRestartClientIds.Clear();
                ClearDisconnectedPlayersData();

                m_gameState.Value = GameState.GameScreenLoadingEveryoneIn;
                NetworkManager.Singleton.SceneManager.LoadScene("WhoseSongIsItScene", UnityEngine.SceneManagement.LoadSceneMode.Single);
            }
        }

        void OnRoundEnded(RoundResults results)
        {
            m_gameState.Value = GameState.GameScreenWaitingToContinueNextRound;
        }

        public bool HasEndGameResults()
        {
            return m_currentGameFinalResults.Value.PlayerResults != null && m_currentGameFinalResults.Value.PlayerResults.Length > 0;
        }
    }
}