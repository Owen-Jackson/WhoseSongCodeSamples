using SpotifyAPI.Web;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace GOJ.Game.Gamemodes
{
    [RequireComponent(typeof(RoundTimer))]
    public class WhoseSongGamemode : Gamemode, ITimedMode, IScoreBasedMode
    {
        public static event Action<FullTrack> OnReceivedNewTrack;
        public static event Action<Gamemode> OnVotesRequested;

        [SerializeField]
        public NetworkVariable<FixedString128Bytes> CurrentTrackId { get; private set; } = new NetworkVariable<FixedString128Bytes>();

        [SerializeField]
        int m_scoreToWin = 30;
        public int ScoreToWin
        {
            get { return m_scoreToWin; }
        }

        public FullTrack CurrentTrack { get; private set; }
        public RoundTimer Timer { get; private set; }

        HashSet<string> m_playersWithCurrentTrack = new HashSet<string>();

        /// <summary>
        /// Wait until all players have sent their results to the server with this before distributing
        /// </summary>
        int m_playerResultsReadyCount = 0;        

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {

                Timer = GetComponent<RoundTimer>();

                RoundTimer.OnTimerReachedZero += OnGuessTimerExpired;

                CurrentTrackId.Value = "";

                GameSessionManager.Instance.SetGamemode(this);
            }
        }

        public override void OnNetworkDespawn()
        {
            if(!IsServer)
                return;

            RoundTimer.OnTimerReachedZero -= OnGuessTimerExpired;
        }

        void OnGuessTimerExpired()
        {
            List<ulong> notReceivedVotesFromClientIds = new List<ulong>();
            //ask player ids who haven't voted to send their current votes
            foreach (var player in GameSessionManager.Instance.PlayerData)
            {
                if (m_submittedClientIds.Contains(player.Value.ClientId) || !player.Value.IsInThisRound)
                    continue;

                notReceivedVotesFromClientIds.Add(player.Value.ClientId);
            }

            if (notReceivedVotesFromClientIds.Count > 0)
            {
                ClientRpcParams rpcParams = new ClientRpcParams();
                rpcParams.Send.TargetClientIds = notReceivedVotesFromClientIds;
                RequestPlayerVotesClientRpc(rpcParams);
            }
            else
            {
                //send out event saying time up, gather guess results
                CalculateAndDistributeResults();
            }
        }

        [ClientRpc]
        void RequestPlayerVotesClientRpc(ClientRpcParams clientRpcParams = default)
        {
            OnVotesRequested?.Invoke(this);
        }

        public override void StartNextRound()
        {
            m_hasCalculatedResultsThisRound = false;

            foreach (var playerVotes in m_playerVotes)
            {
                playerVotes.Value.Clear();
            }

            m_submittedClientIds.Clear();

            SetupRoundQuestion();
        }

        protected override async void SetupRoundQuestion()
        {
            ProcessReceivedTrack(await PlayerTrackDatabase.Instance.GetRandomTrack());

            m_playersWithCurrentTrack.Clear();

            byte[] trackBytes = Helpers.NetworkConversionHelpers.ObjectToBytes(CurrentTrack);

            m_playerResultsReadyCount = 0;

            NewRoundStartedClientRpc(trackBytes);
            HasTrackClientRpc(new FixedString512Bytes(CurrentTrack.Id));
        }

        protected override void ProcessReceivedTrack(FullTrack fullTrack)
        {
            CurrentTrack = fullTrack;
            CurrentTrackId.Value = CurrentTrack != null ? CurrentTrack.Id : "NO_TRACK";
        }

        public async override Task<byte[]> CalculateResultsForRound()
        {
            if (CurrentTrack != null)
            {
                while(GameSessionManager.Instance != null && m_playerResultsReadyCount < GameSessionManager.Instance.ActivePlayers.Count)
                {
                    await Task.Delay(1000);
                }
            }
            else
            {
                Logger.LogError("current track is null, not checking players with track");
            }

            Dictionary<string, PlayerRoundResult> resultsDictionary = new Dictionary<string, PlayerRoundResult>();

            //update player's scores server-side
            foreach (var playerVotes in m_playerVotes)
            {
                string votingPlayerId = playerVotes.Key;

                List<PlayerGuess> guessesByThisPlayer = new List<PlayerGuess>();

                int scoreToAdd = 0;
                foreach (var vote in playerVotes.Value)
                {
                    bool isCorrect = m_playersWithCurrentTrack.Contains(vote);

                    PlayerSessionData player = GameSessionManager.Instance.GetPlayerData(vote);
                    string name = player.PlayerName.ToString();

                    PlayerGuess playerGuess = new PlayerGuess(player.PlayerId, name, isCorrect);

                    guessesByThisPlayer.Add(playerGuess);

                    if (isCorrect)
                        scoreToAdd += 3;
                    else
                        scoreToAdd -= 1;
                }

                if (PlayerScores.ContainsKey(votingPlayerId))
                {
                    PlayerScores[votingPlayerId] += scoreToAdd;
                    Logger.LogFormat("adding {0} point to {1}, new score = {2}", scoreToAdd, votingPlayerId, PlayerScores[votingPlayerId]);
                }
                else
                {
                    PlayerScores.Add(votingPlayerId, scoreToAdd);
                    Logger.LogFormat("adding {0} point to {1}, new score = {2}", scoreToAdd, votingPlayerId, PlayerScores[votingPlayerId]);
                }

                PlayerRoundResult playerRoundResult = new PlayerRoundResult(scoreToAdd, PlayerScores[votingPlayerId], guessesByThisPlayer);
                resultsDictionary.Add(votingPlayerId, playerRoundResult);
            }

            RoundResults finalResult = new RoundResults(new HashSet<string>(m_playersWithCurrentTrack), new Dictionary<string, PlayerRoundResult>(resultsDictionary));
            Logger.Log($"results for this round: {finalResult}");

            byte[] resultBytes = Helpers.NetworkConversionHelpers.ObjectToBytes(finalResult);

            return resultBytes;
        }

        public override string CheckIfPlayerHasWon()
        {
            string winner = null;
            int highestScore = -1;
            foreach (var player in PlayerScores)
            {
                if (player.Value < ScoreToWin || player.Value < highestScore)
                    continue;

                if (player.Value == highestScore)
                {
                    winner = null;
                }
                else
                {
                    winner = player.Key;
                }

                highestScore = Mathf.Max(player.Value, highestScore);
            }

            return winner;
        }
    
        [ClientRpc]
        public void HasTrackClientRpc(FixedString512Bytes trackId, ClientRpcParams clientRpcParams = default)
        {
            bool hasTrack = TrackRetrievalManager.Instance.MyTracks.ContainsKey(trackId.ToString());

            Logger.Log($"does {GameManager.Instance.LocalPlayerId} have current track with id {trackId} ? : {hasTrack}");

            PlayerHasTrackServerRpc(hasTrack);
        }

        [ServerRpc(RequireOwnership = false)]
        public void PlayerHasTrackServerRpc(bool hasTrack, ServerRpcParams serverRpcParams = default)
        {
            if (hasTrack)
            {
                m_playersWithCurrentTrack.Add(GameSessionManager.Instance.ClientIdToPlayerId[serverRpcParams.Receive.SenderClientId]);
            }

            m_playerResultsReadyCount++;
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestCurrentTrackServerRpc(ServerRpcParams serverRpcParams = default)
        {
            ClientRpcParams clientRpcParams = new ClientRpcParams();
            clientRpcParams.Send.TargetClientIds = new List<ulong>() { serverRpcParams.Receive.SenderClientId };

            byte[] currentTrackBytes = Helpers.NetworkConversionHelpers.ObjectToBytes(CurrentTrack);

            RequestCurrentTrackResponseClientRpc(currentTrackBytes, clientRpcParams);
        }

        [ClientRpc]
        void RequestCurrentTrackResponseClientRpc(byte[] fullTrackBytes, ClientRpcParams clientRpcParams = default)
        {
            FullTrack track = Helpers.NetworkConversionHelpers.BytesToObject<FullTrack>(fullTrackBytes);

            OnReceivedNewTrack?.Invoke(track);
        }
    }
}