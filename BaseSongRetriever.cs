using Newtonsoft.Json;
using SpotifyAPI.Web;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace GOJ.Game
{
    public abstract class BaseSongRetriever : ISongRetriever
    {
        /// <summary>
        /// Edit this value to tune how often to get a new set of tracks from the user
        /// </summary>
        protected static TimeSpan TimeBetweenFetchingNewSetFromSpotify = new TimeSpan(14, 0, 0, 0);

        protected TrackRetrievalManager.UserTrackOrigins m_trackOrigin;
        protected readonly string m_folderPath = Path.Combine(Application.persistentDataPath, "RetrievedTracks");
        protected virtual string FilePath => Path.Combine(m_folderPath, $"{m_trackOrigin}.json");
        protected virtual string PlayerPrefsPath => m_trackOrigin.ToString();

        public BaseSongRetriever(TrackRetrievalManager.UserTrackOrigins trackOrigin) 
        {
            m_trackOrigin = trackOrigin;
        }

        public async Task<(IEnumerable<FullTrack> tracks, TrackRetrievalManager.UserTrackOrigins origin)> RetrieveSongs(bool includeExplicit)
        {
            IEnumerable<FullTrack> tracks = null;

            if (ShouldGetTracksFromSpotify())
            {
                tracks = await GetTracksFromSpotify();
                SaveTracksLocally(tracks);
            }
            else
            {
                tracks = await LoadTracksLocally();
            }

            if (!includeExplicit)
                tracks = tracks.Where(x => x != null && !x.Explicit);

            tracks = tracks.Where(x => !string.IsNullOrEmpty(x.Name));

            return (tracks, m_trackOrigin);
        }

        protected abstract Task<IEnumerable<FullTrack>> GetTracksFromSpotify();

        protected virtual void SaveTracksLocally(IEnumerable<FullTrack> tracks)
        {
            if(!Directory.Exists(m_folderPath))
                Directory.CreateDirectory(m_folderPath);

            string data = JsonConvert.SerializeObject(tracks);
            File.WriteAllText(FilePath, data);
            PlayerPrefs.SetString($"{PlayerPrefsPath}LastFetchedDateTime", DateTime.UtcNow.ToString());
        }

        protected virtual async Task<List<FullTrack>> LoadTracksLocally()
        {
            List<FullTrack> tracks = new List<FullTrack>();

            if (!Directory.Exists(m_folderPath))
            {
                Logger.LogError($"Directory {m_folderPath} doesn't exist, not loading any tracks for {m_trackOrigin}");
                return tracks;
            }

            if (File.Exists(FilePath))
            {
                string data = File.ReadAllText(FilePath);

                //Fallback incase the created file is empty
                if (string.IsNullOrEmpty(data))
                {
                    tracks = await GetTracksFromSpotify() as List<FullTrack>;
                    SaveTracksLocally(tracks);
                }
                else
                {
                    tracks = JsonConvert.DeserializeObject<List<FullTrack>>(data);
                }
            }
            else
            {
                Logger.LogError($"File {FilePath} does not exist.");
            }

            return tracks;
        }

        protected virtual bool ShouldGetTracksFromSpotify()
        {
            if (GameManager.Instance.DebugAlwaysRetrieveFromSpotify)
                return true;

            //Need to fetch from Spotify and make the local save data file
            if (!File.Exists(FilePath))
                return true;
            
            //Need to save the last saved timestamp to PlayerPrefs
            if (!PlayerPrefs.HasKey($"{PlayerPrefsPath}LastFetchedDateTime"))
                return true;

            if (DateTime.TryParse(PlayerPrefs.GetString($"{PlayerPrefsPath}LastFetchedDateTime"), out var lastSavedTimestamp))
                return DateTime.UtcNow - lastSavedTimestamp >= TimeBetweenFetchingNewSetFromSpotify;
            else
                return true;
        }
    }
}