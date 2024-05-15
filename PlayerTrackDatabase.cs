using SpotifyAPI.Web;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace GOJ.Game
{
    public class PlayerTrackDatabase
    {
        /// <summary>
        /// 0-1f percentage chance to select a song with some player crossover
        /// </summary>
        const float m_songsWithMultipleOwnersWeight = 0.7f;

        /// <summary>
        /// Sent by all players on joining the game, based on the game settings set by the host
        /// </summary>
        Dictionary<string, TrackInfo> m_tracksForCurrentGame = new Dictionary<string, TrackInfo>();
        public Dictionary<string, TrackInfo> TracksForCurrentGame { get {  return m_tracksForCurrentGame; } }

        /// <summary>
        /// Any tracks played by more than one player will get added here, more interesting when there is some crossover
        /// </summary>
        Dictionary<string, TrackInfo> m_unusedTracksWithMultipleOwners = new Dictionary<string, TrackInfo>();

        /// <summary>
        /// <para>Global set of tracks that have been played while the game has been open</para>
        /// <para>TODO: save this before quitting so it can be used across multiple sessions (with timestamp for when to reset)</para>
        /// </summary>
        HashSet<string> m_tracksUsedThisSession = new HashSet<string>();

        private static PlayerTrackDatabase s_instance = null;
        public static PlayerTrackDatabase Instance 
        { 
            get
            {
                if (s_instance == null)
                    s_instance = new PlayerTrackDatabase();

                return s_instance;
            }
        }

        public void ResetTracksForCurrentGame()
        {
            if (m_tracksForCurrentGame == null)
                m_tracksForCurrentGame = new Dictionary<string, TrackInfo>();
            else
                m_tracksForCurrentGame.Clear();
        }

        public async Task<FullTrack> GetRandomTrack()
        {
            FullTrack result;

            if(m_unusedTracksWithMultipleOwners.Count == 0)
            {
                Logger.LogWarning("no tracks with multiple owners, taking one from the full list");

                result = await GetRandomTrackFromDictionary(m_tracksForCurrentGame, m_tracksUsedThisSession);

                m_tracksUsedThisSession.Add(result.Id);

                return result;
            }

            bool getTrackWithMultiplePeople = Random.Range(0f, 1f) <= m_songsWithMultipleOwnersWeight;

            if (getTrackWithMultiplePeople)
            {
                try
                {
                    result = await GetRandomTrackFromDictionary(m_unusedTracksWithMultipleOwners, m_tracksUsedThisSession);
                    m_unusedTracksWithMultipleOwners.Remove(result.Id);
                    Logger.Log("got multiple owner track and removed from dictionary");
                }
                catch(System.Exception ex)
                {
                    Logger.LogError($"Error getting track with multiple users: {ex}");
                    result = await GetRandomTrackFromDictionary(m_tracksForCurrentGame, m_tracksUsedThisSession);
                }
            }
            else
            {
                result = await GetRandomTrackFromDictionary(m_tracksForCurrentGame, m_tracksUsedThisSession);
            }

            m_tracksUsedThisSession.Add(result.Id);

            return result;
        }

        async Task<FullTrack> GetRandomTrackFromDictionary(Dictionary<string, TrackInfo> dictionary, HashSet<string> playedTrackIds)
        {
            if (dictionary == null || dictionary.Count == 0)
                throw new System.Exception("Track dictionary is null or empty");

            FullTrack track;

            string resultId = null;

            int maxAttempts = 30;
            int currentAttempt = 0;

            do
            {
                int trackIndex = Random.Range(0, dictionary.Count);

                var kvp = dictionary.ElementAt(trackIndex);
                if (kvp.Value == null || string.IsNullOrEmpty(kvp.Value.TrackId))
                    continue;

                resultId = dictionary.ElementAt(trackIndex).Value.TrackId;
                
                if(currentAttempt++ >= maxAttempts)
                {
                    throw new System.Exception("Too many attempts to find shared track, ending loop before it goes infinite");
                }
            } while (playedTrackIds != null && playedTrackIds.Contains(resultId));

            track = await Helpers.SpotifyEndpointHelpers.GetTrack(resultId);

            return track;
        }

        public void CalculateTracksWithMultipleOwners()
        {
            m_unusedTracksWithMultipleOwners = new Dictionary<string, TrackInfo>();

            foreach(var trackPair in m_tracksForCurrentGame)
            {
                if(!m_tracksUsedThisSession.Contains(trackPair.Key) && trackPair.Value.PlayersWithTrack.Count > 1)
                {
                    m_unusedTracksWithMultipleOwners[trackPair.Key] = trackPair.Value;
                }
            }

            Logger.Log($"unused tracks with multiple owners count: {m_unusedTracksWithMultipleOwners.Count}");
        }
    }
} 