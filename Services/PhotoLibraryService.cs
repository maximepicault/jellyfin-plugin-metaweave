using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.CustomMetadata.Api;
using Jellyfin.Plugin.CustomMetadata.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>What a source says about a photo, and the tag it was found by (null when found by file name in its album)</summary>
    public sealed record PhotoMatch(CustomMetadataResponse Data, string OwnTag);

    /// <summary>
    /// Photo albums: finds the source of a tagged album folder and of its photos. An album is identified by the {prefix-id} tag of
    /// its OWN folder name (sub-albums carry their own). A photo is identified by a tag in its own file name (API sources), else by
    /// its file name in the list of photos of its album's answer. Only sources whose content includes albums answer.
    /// </summary>
    public class PhotoLibraryService
    {
        // A refresh asks about every photo of an album one after the other: one call to the source serves them all
        private static readonly TimeSpan AnswerLifetime = TimeSpan.FromSeconds(30);
        private const int MaxRememberedAnswers = 200;

        private readonly CustomApiClient _api;
        private readonly SourceResolver _resolver;
        private readonly ILogger _logger;
        private readonly JsonFieldMapper _mapper;
        private readonly ConcurrentDictionary<string, (DateTime Until, Task<string> Json)> _answers = new ConcurrentDictionary<string, (DateTime, Task<string>)>(StringComparer.OrdinalIgnoreCase);

        public PhotoLibraryService(CustomApiClient api, SourceResolver resolver, ILogger logger)
        {
            _api = api;
            _resolver = resolver;
            _logger = logger;
            _mapper = new JsonFieldMapper(logger);
        }

        /// <summary>The albums source of a tag and the id without its prefix, or null</summary>
        private (SourceConfiguration Source, string Id)? Resolve(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return null;
            }

            var source = _resolver.GetSourceByPrefix(_resolver.ExtractPrefixFromId(tag), SourceContent.Albums);
            return source == null ? null : (source, _resolver.ExtractIdWithoutPrefix(tag));
        }

        private static string OwnTag(string path) => string.IsNullOrEmpty(path) ? null : TagParser.Extract(TagParser.GetOwnName(path));

        /// <summary>Metadata of the album folder at this path: its own tag, else the id typed in Jellyfin</summary>
        public async Task<(CustomMetadataResponse Data, string Tag)?> GetAlbumAsync(string path, IReadOnlyDictionary<string, string> providerIds, bool bypassCache, CancellationToken cancellationToken)
        {
            var tag = OwnTag(path) ?? CustomId.Get(providerIds);
            if (Resolve(tag) is not { } found)
            {
                return null;
            }

            if (bypassCache)
            {
                // A user refresh: the photos of this album, asked about next, must not get an answer from before it
                _answers.TryRemove(Key(found.Source, found.Id), out _);
            }

            var data = await _api.GetMetadataAsync(found.Id, "album", found.Source, cancellationToken, bypassCache);
            if (data == null)
            {
                _logger.LogWarning("📸 [ALBUM] ⚠️ No metadata returned for {Tag}", tag);
                return null;
            }

            return (data, tag);
        }

        /// <summary>What the sources say about the photo at this path, album tags included when the source asks for it; null = leave it alone</summary>
        public async Task<PhotoMatch> GetPhotoAsync(string path, IReadOnlyDictionary<string, string> providerIds, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var albumTag = OwnTag(System.IO.Path.GetDirectoryName(path));
            var album = Resolve(albumTag);

            // The answer of the photo's own album: its list of photos, and its tags for the option PhotoInheritAlbumTags
            async Task<(ListedPhoto Listed, List<string> AlbumTags)> FromAlbum()
            {
                if (album is not { } parent)
                {
                    return default;
                }

                var json = await AlbumJson(parent.Source, parent.Id, cancellationToken);
                if (json == null)
                {
                    return default;
                }

                try
                {
                    using var document = JsonDocument.Parse(json);
                    var file = System.IO.Path.GetFileName(path);
                    var listed = PhotoList.Read(document.RootElement, parent.Source, _mapper)?.FirstOrDefault(p => string.Equals(p.File, file, StringComparison.OrdinalIgnoreCase));
                    var tags = parent.Source.PhotoInheritAlbumTags
                        ? _mapper.MapToMetadata(document, parent.Source.IsLocal ? new FieldMapping() : parent.Source.AlbumFieldMappings ?? new FieldMapping(), true)?.Tags
                        : null;
                    return (listed, tags);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("📸 [PHOTO] ⚠️ The answer of album {Tag} is not valid JSON: {Message}", albumTag, ex.Message);
                    return default;
                }
            }

            // 1. The photo's own tag (an API source with a photo endpoint)
            var ownTag = OwnTag(path) ?? CustomId.Get(providerIds);
            if (Resolve(ownTag) is { } own && !own.Source.IsLocal && !string.IsNullOrWhiteSpace(own.Source.PhotoEndpoint))
            {
                var data = await _api.GetMetadataAsync(own.Id, "photo", own.Source, cancellationToken, bypassCache: true);
                if (data != null)
                {
                    data.InheritedTags = (await FromAlbum()).AlbumTags;
                    return new PhotoMatch(data, ownTag);
                }
            }

            // 2. Its file name in its album's list
            var (entry, albumTags) = await FromAlbum();
            if (entry == null && !(albumTags?.Count > 0))
            {
                return null;
            }

            var found = entry?.Data ?? new CustomMetadataResponse();
            found.InheritedTags = albumTags;
            return new PhotoMatch(found, null);
        }

        private static string Key(SourceConfiguration source, string id) => $"{source.Prefix}|{source.BaseUrl}|{id}";

        private Task<string> AlbumJson(SourceConfiguration source, string id, CancellationToken cancellationToken)
        {
            // A local source is a file of this server: always read, so that an edit shows at once
            if (source.IsLocal)
            {
                return _api.GetAlbumJsonAsync(id, source, cancellationToken);
            }

            var key = Key(source, id);
            var now = DateTime.UtcNow;
            if (_answers.TryGetValue(key, out var known) && known.Until > now && !known.Json.IsFaulted && !known.Json.IsCanceled)
            {
                return known.Json;
            }

            if (_answers.Count >= MaxRememberedAnswers)
            {
                _answers.Clear();
            }

            var answer = _api.GetAlbumJsonAsync(id, source, cancellationToken);
            _answers[key] = (now + AnswerLifetime, answer);
            return answer;
        }
    }
}
