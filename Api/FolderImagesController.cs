using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Api
{
    /// <summary>
    /// Images kept in the image sub-folders (Primary, Backdrop, Logo, Banner, Thumb) of a series or season folder.
    /// GET serves them to Jellyfin's image picker (preview + download); POST copies them into the gallery of a local node.
    /// </summary>
    [ApiController]
    [Route("CustomMetadata/FolderImages")]
    public class FolderImagesController : ControllerBase
    {
        private readonly ILibraryManager _library;
        private readonly LocalSourceStore _store;
        private readonly ILogger<FolderImagesController> _logger;

        public FolderImagesController(ILibraryManager library, LocalSourceStore store, ILogger<FolderImagesController> logger)
        {
            _library = library;
            _store = store;
            _logger = logger;
        }

        /// <summary>
        /// One image of a sub-folder. Anonymous because jellyfin-web previews it with an img tag (no token): it only serves files
        /// that sit in an image sub-folder of a series or season folder of the library, never any other path.
        /// </summary>
        [HttpGet("{itemId}/{folder}/{file}")]
        [AllowAnonymous]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult Get(Guid itemId, string folder, string file)
        {
            if (!TryRead(_library, itemId, folder, file, out var path, out var mime))
            {
                return NotFound();
            }

            Response.Headers["Cache-Control"] = "private, max-age=600";
            return PhysicalFile(path, mime);
        }

        internal static bool TryRead(ILibraryManager library, Guid itemId, string folder, string file, out string path, out string mime)
        {
            path = null;
            mime = null;
            var item = library.GetItemById(itemId);
            return (item is Series || item is Season) && FolderImages.TryResolve(item.Path, folder, file, out path, out mime);
        }

        /// <summary>
        /// Copies the images of the sub-folders of the series or season carrying {Tag} into the plugin's image storage and returns them
        /// as gallery entries (the editor merges them: an entry already imported, same Name, is skipped there). Saves no content.
        /// </summary>
        [HttpPost("Import")]
        [Authorize(Policy = "RequiresElevation")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<FolderImportResult> Import([FromBody] FolderImportRequest request)
        {
            var tag = (request?.Tag ?? string.Empty).Trim().Trim('{', '}');
            if (tag.Length == 0 || !LocalSourceStore.IsValidDataId(request.DataId ?? string.Empty))
            {
                return BadRequest("Tag ou identifiant de données manquant");
            }

            var items = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Season }, Recursive = true })
                .Where(i => SourceRefreshService.TagsOf(i).Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var result = new FolderImportResult { Items = items.Count };
            foreach (var item in items)
            {
                foreach (var image in FolderImages.Discover(item.Path))
                {
                    try
                    {
                        var url = _store.SaveImage(request.DataId, request.NodeId, System.IO.File.ReadAllBytes(image.FullPath), out var error);
                        if (url == null)
                        {
                            result.Refused.Add(image.Name + " : " + error);
                            continue;
                        }

                        result.Images.Add(new FolderImportImage { Type = image.Type, Url = url, Name = image.Name });
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "🖼️ [FOLDER IMAGES] ⚠️ Could not read {Path}", image.FullPath);
                        result.Refused.Add(image.Name + " : illisible");
                    }
                }
            }

            _logger.LogInformation("🖼️ [FOLDER IMAGES] Import for '{Tag}': {Items} item(s), {Images} image(s), {Refused} refused", tag, result.Items, result.Images.Count, result.Refused.Count);
            return Ok(result);
        }
    }

    public class FolderImportRequest
    {
        public string Tag { get; set; }
        public string DataId { get; set; }
        public string NodeId { get; set; }
    }

    public class FolderImportImage
    {
        public string Type { get; set; }
        public string Url { get; set; }
        public string Name { get; set; }
    }

    public class FolderImportResult
    {
        /// <summary>How many library items (series or seasons) carry the tag</summary>
        public int Items { get; set; }
        public List<FolderImportImage> Images { get; set; } = new List<FolderImportImage>();
        public List<string> Refused { get; set; } = new List<string>();
    }
}
