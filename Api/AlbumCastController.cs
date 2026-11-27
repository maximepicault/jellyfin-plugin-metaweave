using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// The people of albums, photos and videos, for the theme's screens. Any signed-in user, over what that user sees: an item
    /// they cannot see is never named, counted or listed.
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/AlbumCast")]
    [Authorize]
    public class AlbumCastController : ControllerBase
    {
        private const string UserIdClaim = "Jellyfin-UserId";
        private const string IsApiKeyClaim = "Jellyfin-IsApiKey";

        private readonly AlbumCastStore _store;
        private readonly ILibraryManager _library;
        private readonly IUserManager _users;

        public AlbumCastController(AlbumCastStore store, ILibraryManager library, IUserManager users)
        {
            _store = store;
            _library = library;
            _users = users;
        }

        /// <summary>A function telling the item of an id when the caller may see it, else null</summary>
        private Func<Guid, BaseItem> Visible()
        {
            if (bool.TryParse(User.FindFirst(IsApiKeyClaim)?.Value, out var apiKey) && apiKey)
            {
                return id => id == Guid.Empty ? null : _library.GetItemById<BaseItem>(id);
            }

            // ILibraryManager skips the access check when the user is null: an empty or unknown user sees nothing
            var user = Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out var userId) && userId != Guid.Empty ? _users.GetUserById(userId) : null;
            return id => user == null || id == Guid.Empty ? null : _library.GetItemById<BaseItem>(id, user);
        }

        /// <summary>With « Importer le casting » off, the links are kept but not shown</summary>
        private static bool CastingOn => Plugin.Instance?.Configuration?.EnableCasting ?? true;

        /// <summary>The recorded items the caller sees, below one library when given</summary>
        private IEnumerable<(BaseItem Item, AlbumCastRecord Record)> Seen(Guid parentId)
        {
            if (!CastingOn)
            {
                yield break;
            }

            var visible = Visible();
            foreach (var (id, record) in _store.All())
            {
                var item = visible(id);
                if (item != null && (parentId == Guid.Empty || _library.GetCollectionFolders(item).Any(f => f.Id == parentId)))
                {
                    yield return (item, record);
                }
            }
        }

        [HttpGet("Item/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<List<CastMember>> Item(Guid itemId)
            => Visible()(itemId) == null ? NotFound() : Ok((CastingOn ? _store.Get(itemId)?.Members : null) ?? new List<CastMember>());

        /// <summary>Who appears in a library, with the number of their albums, photos and videos</summary>
        [HttpGet("People")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<List<AlbumPerson>> People([FromQuery] Guid parentId)
            => Ok(Seen(parentId)
                .SelectMany(x => x.Record.Members.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new AlbumPerson { Name = g.First().Name, Image = g.Select(m => m.Image).FirstOrDefault(i => !string.IsNullOrEmpty(i)), Count = g.Count() })
                .OrderByDescending(p => p.Count).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList());

        /// <summary>Ids of the albums, photos and videos a person is linked to</summary>
        [HttpGet("Items")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<List<string>> Items([FromQuery] string person, [FromQuery] Guid parentId)
            => Ok(Seen(parentId)
                .Where(x => x.Record.Members.Any(m => string.Equals(m.Name, person?.Trim(), StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Item.IsFolder ? 0 : 1).ThenBy(x => x.Item.SortName, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Item.Id.ToString("N"))
                .ToList());
    }

    public class AlbumPerson
    {
        public string Name { get; set; }
        public string Image { get; set; }
        public int Count { get; set; }
    }
}
