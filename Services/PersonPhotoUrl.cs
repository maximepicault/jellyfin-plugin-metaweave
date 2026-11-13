using System;

namespace Jellyfin.Plugin.CustomMetadata.Services
{
    /// <summary>
    /// The photo of a person is often not an address but a PATH ("/abc123.jpg") to complete with the address of an
    /// image server, which is not the API's. "PhotoBaseUrl" of the mapping says how:
    /// - "https://img.example.com/p/w185"          → prefix: the path is appended ("…/p/w185/abc123.jpg")
    /// - "https://img.example.com/{value}?w=200"   → template: {value} is replaced by the path
    /// Empty = the old behaviour (a path starting with "/" is completed with the API's base URL).
    /// A value that is already an address (http:// or https://) is left as it is.
    /// </summary>
    public static class PersonPhotoUrl
    {
        public static string Resolve(string photo, string photoBaseUrl, string apiBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(photo))
            {
                return null;
            }

            photo = photo.Trim();
            var isAddress = photo.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || photo.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || photo.StartsWith("//", StringComparison.Ordinal);
            if (isAddress || string.IsNullOrWhiteSpace(photoBaseUrl))
            {
                return PreviewMapper.ResolveUrl(photo, apiBaseUrl);
            }

            var template = photoBaseUrl.Trim();
            var at = template.IndexOf("{value}", StringComparison.Ordinal);
            if (at >= 0)
            {
                // "…/{value}" + "/abc.jpg" must not give a double slash
                var value = at > 0 && template[at - 1] == '/' ? photo.TrimStart('/') : photo;
                return template.Replace("{value}", value);
            }

            return template.TrimEnd('/') + "/" + photo.TrimStart('/');
        }
    }
}
