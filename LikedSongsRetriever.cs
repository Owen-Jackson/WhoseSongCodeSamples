using SpotifyAPI.Web;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace GOJ.Game
{
    public class LikedSongsRetriever : BaseSongRetriever
    {
        public LikedSongsRetriever() : base(TrackRetrievalManager.UserTrackOrigins.Liked) { }

        protected override async Task<IEnumerable<FullTrack>> GetTracksFromSpotify()
        {
            LibraryTracksRequest request = new LibraryTracksRequest()
            {
                Limit = 50
            };

            Paging<SavedTrack> paging = await Helpers.SpotifyEndpointHelpers.GetUserSavedTracks(request);

            var savedTracks = await S4UUtility.GetAllOfPagingAsync(SpotifyService.Instance.GetSpotifyClient(), paging, ISongRetriever.MAX_TRACKS);

            return savedTracks.Take(ISongRetriever.MAX_TRACKS).Select(x => x.Track);
        }
    }
}