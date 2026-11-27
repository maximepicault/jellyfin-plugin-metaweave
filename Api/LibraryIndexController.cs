using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// The theme's filtered lists (« Index ») ask here for one page of results and the filters on offer, so that a big
    /// library is never sent to the browser. Any signed-in user, over the items that user may see. The items themselves
    /// are then read by the browser from Jellyfin's own API.
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/Index")]
    [Authorize]
    public class LibraryIndexController : ControllerBase
    {
        // claim set by Jellyfin's authentication handler
        private const string UserIdClaim = "Jellyfin-UserId";

        private readonly LibraryIndexService _index;
        private readonly IUserManager _users;

        public LibraryIndexController(LibraryIndexService index, IUserManager users)
        {
            _index = index;
            _users = users;
        }

        [HttpPost]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<IndexResponse> Query([FromBody] IndexRequest request)
        {
            var user = Guid.TryParse(User.FindFirst(UserIdClaim)?.Value, out var id) && id != Guid.Empty ? _users.GetUserById(id) : null;
            if (user == null || request == null)
            {
                // an API key has no user: there would be nobody whose rights and watched states to apply
                return BadRequest();
            }

            var snapshot = _index.Snapshot();
            var visible = _index.Visible(user, request.ParentId ?? Guid.Empty, snapshot);
            if (visible == null)
            {
                return NotFound();
            }

            var entries = snapshot.Entries.Where(e => visible.Contains(e.Id) && (user.DisplayMissingEpisodes || !e.IsVirtual)).ToList();
            var query = new LibraryIndexQuery(entries, snapshot.ById, _index.Watched(user.Id), request);

            var results = query.Results();
            var playable = query.Playable(results);
            var response = new IndexResponse
            {
                Total = results.Count,
                SeriesCount = results.Where(e => e.SeriesId.HasValue).Select(e => e.SeriesId.Value).Distinct().Count(),
                Playable = playable.Count > 0
            };
            var people = new HashSet<string>(query.Chosen("people"), StringComparer.OrdinalIgnoreCase);

            if (request.Limit > 0)
            {
                var ordered = query.Ordered(results, request.Sort, request.Seed);
                (response.Items, response.Groups) = LibraryIndexQuery.Page(ordered, request.Sort, request.StartIndex, request.Limit);
            }

            if (request.Summary)
            {
                response.Types = query.Types();
                response.Missing = query.Missing();
                response.Suggestions = query.Suggestions(results.Count);
                response.Letters = query.Counts("letter");
                people.UnionWith(response.Suggestions.Where(s => s.Facet == "people").Select(s => s.Value));
            }

            if (request.Facets?.Length > 0)
            {
                response.Facets = request.Facets.Distinct().ToDictionary(f => f, f => query.Facet(f, request.FacetQuery, request.FacetLimit));
                if (response.Facets.TryGetValue("genres", out var genres))
                {
                    query.Pictures(genres.Values);
                }

                if (response.Facets.TryGetValue("people", out var cast))
                {
                    // names without a limit would mean thousands of person lookups
                    cast.Values = cast.Values.Take(LibraryIndexQuery.MaxPage).ToList();
                    people.UnionWith(cast.Values.Select(v => v.Value));
                }
            }

            if (!string.IsNullOrEmpty(request.Play))
            {
                var seed = request.Play == "shuffle" ? Random.Shared.Next() : request.Seed;
                var sort = request.Play == "shuffle" ? "random" : request.Sort;
                response.PlayIds = query.Ordered(playable, sort, seed)
                    // shuffled across the types, not films first then episodes
                    .OrderBy(e => request.Play == "shuffle" ? Random.Shared.Next() : 0)
                    .Take(LibraryIndexQuery.PlayLimit).Select(e => e.Id).ToList();
            }

            response.People = _index.Persons(people).ToDictionary(p => p.Key, p => new IndexPerson { Id = p.Value.Id, HasImage = p.Value.HasImage });
            Response.Headers["Cache-Control"] = "private, no-store";
            return response;
        }
    }
}
