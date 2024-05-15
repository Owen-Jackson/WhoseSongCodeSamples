using SpotifyAPI.Web;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;

namespace GOJ.Game
{
    public class TrackRetrievalManager
    {
        public enum UserTrackOrigins
        {
            RecentlyPlayed,
            Liked,
            Playlist,
            TopList
        }

        static TrackRetrievalManager s_instance;
        public static TrackRetrievalManager Instance
        {
            get
            {
                if (s_instance == null)
                    s_instance = new TrackRetrievalManager();

                return s_instance;
            }
        }

        HashSet<string> m_topPlayedTracks;
        HashSet<string> m_likedTracks;
        HashSet<string> m_tracksInPlaylists;

        PersonalizationTopRequest.TimeRange? m_previousTimeRange = null;

        /// <summary>
        /// TrackId --> full track info, used when checking if I have the track for the current guessing round
        /// </summary>
        public Dictionary<string, FullTrack> MyTracks { get; private set; } = new Dictionary<string, FullTrack>();

        public async void RetrieveTracksForGameSettings(SpotifySettings settings, System.Action<HashSet<string>> OnComplete = null)
        {
            PersonalizationTopRequest topSongsRequestParams = null;

            if (settings.UseTopSongs)
            {
                topSongsRequestParams = GetTopSongsRequest(settings.SelectedTopSongsTimeRange);
            }

            await RetrieveAllMySongs(settings.UseLikedSongs, topSongsRequestParams, settings.UseSongsInPlaylists);            
            
            HashSet<string> tracksForSettings = BuildTrackSetForSettings(settings);

            OnComplete?.Invoke(tracksForSettings);
        }

        PersonalizationTopRequest GetTopSongsRequest(PersonalizationTopRequest.TimeRange timeRange)
        {
            PersonalizationTopRequest result = new PersonalizationTopRequest();
            result.TimeRangeParam = timeRange;

            //top songs currently only does first 50 items
            result.Limit = 50;
            result.Offset = 0;
            return result;
        }

        async Task RetrieveAllMySongs(bool useLikedSongs, PersonalizationTopRequest topSongsRequestParams, bool useSongsInPlaylists)
        {
            List<Task<(IEnumerable<FullTrack> tracks, UserTrackOrigins origin)>> tasks = new List<Task<(IEnumerable<FullTrack>, UserTrackOrigins)>>();

            if(useLikedSongs && m_likedTracks == null)
            {
                m_likedTracks = new HashSet<string>();

                LikedSongsRetriever likedSongsRetriever = new LikedSongsRetriever();
                tasks.Add(likedSongsRetriever.RetrieveSongs(GameManager.Instance.LocalUseExplicitSongs));
            }

            if(topSongsRequestParams != null && (m_topPlayedTracks == null || topSongsRequestParams.TimeRangeParam != m_previousTimeRange))
            {
                m_topPlayedTracks = new HashSet<string>();

                TopSongsRetriever topSongsRetriever = new TopSongsRetriever(topSongsRequestParams);
                tasks.Add(topSongsRetriever.RetrieveSongs(GameManager.Instance.LocalUseExplicitSongs));

                m_previousTimeRange = topSongsRequestParams.TimeRangeParam;
            }

            if(useSongsInPlaylists && m_tracksInPlaylists == null)
            {
                m_tracksInPlaylists = new HashSet<string>();

                PlaylistSongsRetriever playlistSongsRetriever = new PlaylistSongsRetriever();
                tasks.Add(playlistSongsRetriever.RetrieveSongs(GameManager.Instance.LocalUseExplicitSongs));
            }

            var allMySongs = await Task.WhenAll(tasks);

            foreach(var tracksList in allMySongs)
            {
                if (tracksList.tracks != null && tracksList.tracks.Count() > 0)
                {
                    foreach (var track in tracksList.tracks)
                    {
                        AddTrack(tracksList.origin, track);
                    }
                }
            }

            Logger.Log("song retrieval completed");
        }
                
        void AddTrack(UserTrackOrigins origin, FullTrack track)
        {
            if(track == null || string.IsNullOrEmpty(track.Id))
            {
                Logger.LogError("Cannot add track for user, it is null");
                return;
            }

            switch (origin)
            {
                case UserTrackOrigins.Liked:
                    m_likedTracks.Add(track.Id);
                    break;

                case UserTrackOrigins.TopList:
                    m_topPlayedTracks.Add(track.Id);
                    break;

                case UserTrackOrigins.Playlist:
                    m_tracksInPlaylists.Add(track.Id);
                    break;
            }
            
            MyTracks[track.Id] = track;
        }

        HashSet<string> BuildTrackSetForSettings(SpotifySettings spotifySettings)
        {
            HashSet<string> tracks = new HashSet<string>();

            if (spotifySettings.UseSongsInPlaylists)
            {
                tracks.UnionWith(m_tracksInPlaylists);
            }    

            if(spotifySettings.UseTopSongs)
            {
                tracks.UnionWith(m_topPlayedTracks);
            }

            if(spotifySettings.UseLikedSongs)
            {
                tracks.UnionWith(m_likedTracks);
            }

            return tracks;
        }        
    }
}