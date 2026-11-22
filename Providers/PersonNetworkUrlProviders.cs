using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.CustomMetadata.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.CustomMetadata.Providers
{
    /// <summary>
    /// Links on Jellyfin's person page. Jellyfin 10.11 lists them through <see cref="IExternalUrlProvider"/> classes found by
    /// reflection at startup, each giving ONE name to all its links. A network list edited at runtime therefore goes through a
    /// fixed pool of numbered slots: slot N serves the N-th network of <see cref="PersonNetworkStore"/> and reads it on every call.
    /// </summary>
    public abstract class PersonNetworkUrlProvider : IExternalUrlProvider
    {
        private readonly PersonNetworkStore _networks;
        private readonly PeopleStore _people;
        private readonly int _slot;

        protected PersonNetworkUrlProvider(PersonNetworkStore networks, PeopleStore people, int slot)
        {
            _networks = networks;
            _people = people;
            _slot = slot;
        }

        /// <inheritdoc />
        public string Name
        {
            get
            {
                var network = Network();
                // An image icon (put in front of the link by the plugin's style sheet) replaces the emoji
                return network == null ? "Custom" : (string.IsNullOrEmpty(network.Icon) || network.IconFile != null ? network.Name : network.Icon + " " + network.Name);
            }
        }

        private PersonNetwork Network()
        {
            var all = _networks.Load();
            return _slot < all.Count ? all[_slot] : null;
        }

        /// <inheritdoc />
        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            var network = Network();
            if (network == null || item is not MediaBrowser.Controller.Entities.Person)
            {
                return Enumerable.Empty<string>();
            }

            var match = _people.FindMatch(null, null, item.Name)?.Person;
            return (match?.Networks ?? new List<PersonNetworkValue>())
                .Where(v => string.Equals(v.NetworkId, network.Id, StringComparison.OrdinalIgnoreCase))
                .Select(v => PersonNetworkStore.BuildUrl(network, v.Value))
                .Where(u => u != null)
                .Distinct()
                .ToList();
        }
    }

    public sealed class PersonNetworkSlot00 : PersonNetworkUrlProvider { public PersonNetworkSlot00(PersonNetworkStore n, PeopleStore p) : base(n, p, 0) { } }
    public sealed class PersonNetworkSlot01 : PersonNetworkUrlProvider { public PersonNetworkSlot01(PersonNetworkStore n, PeopleStore p) : base(n, p, 1) { } }
    public sealed class PersonNetworkSlot02 : PersonNetworkUrlProvider { public PersonNetworkSlot02(PersonNetworkStore n, PeopleStore p) : base(n, p, 2) { } }
    public sealed class PersonNetworkSlot03 : PersonNetworkUrlProvider { public PersonNetworkSlot03(PersonNetworkStore n, PeopleStore p) : base(n, p, 3) { } }
    public sealed class PersonNetworkSlot04 : PersonNetworkUrlProvider { public PersonNetworkSlot04(PersonNetworkStore n, PeopleStore p) : base(n, p, 4) { } }
    public sealed class PersonNetworkSlot05 : PersonNetworkUrlProvider { public PersonNetworkSlot05(PersonNetworkStore n, PeopleStore p) : base(n, p, 5) { } }
    public sealed class PersonNetworkSlot06 : PersonNetworkUrlProvider { public PersonNetworkSlot06(PersonNetworkStore n, PeopleStore p) : base(n, p, 6) { } }
    public sealed class PersonNetworkSlot07 : PersonNetworkUrlProvider { public PersonNetworkSlot07(PersonNetworkStore n, PeopleStore p) : base(n, p, 7) { } }
    public sealed class PersonNetworkSlot08 : PersonNetworkUrlProvider { public PersonNetworkSlot08(PersonNetworkStore n, PeopleStore p) : base(n, p, 8) { } }
    public sealed class PersonNetworkSlot09 : PersonNetworkUrlProvider { public PersonNetworkSlot09(PersonNetworkStore n, PeopleStore p) : base(n, p, 9) { } }
    public sealed class PersonNetworkSlot10 : PersonNetworkUrlProvider { public PersonNetworkSlot10(PersonNetworkStore n, PeopleStore p) : base(n, p, 10) { } }
    public sealed class PersonNetworkSlot11 : PersonNetworkUrlProvider { public PersonNetworkSlot11(PersonNetworkStore n, PeopleStore p) : base(n, p, 11) { } }
    public sealed class PersonNetworkSlot12 : PersonNetworkUrlProvider { public PersonNetworkSlot12(PersonNetworkStore n, PeopleStore p) : base(n, p, 12) { } }
    public sealed class PersonNetworkSlot13 : PersonNetworkUrlProvider { public PersonNetworkSlot13(PersonNetworkStore n, PeopleStore p) : base(n, p, 13) { } }
    public sealed class PersonNetworkSlot14 : PersonNetworkUrlProvider { public PersonNetworkSlot14(PersonNetworkStore n, PeopleStore p) : base(n, p, 14) { } }
    public sealed class PersonNetworkSlot15 : PersonNetworkUrlProvider { public PersonNetworkSlot15(PersonNetworkStore n, PeopleStore p) : base(n, p, 15) { } }
    public sealed class PersonNetworkSlot16 : PersonNetworkUrlProvider { public PersonNetworkSlot16(PersonNetworkStore n, PeopleStore p) : base(n, p, 16) { } }
    public sealed class PersonNetworkSlot17 : PersonNetworkUrlProvider { public PersonNetworkSlot17(PersonNetworkStore n, PeopleStore p) : base(n, p, 17) { } }
    public sealed class PersonNetworkSlot18 : PersonNetworkUrlProvider { public PersonNetworkSlot18(PersonNetworkStore n, PeopleStore p) : base(n, p, 18) { } }
    public sealed class PersonNetworkSlot19 : PersonNetworkUrlProvider { public PersonNetworkSlot19(PersonNetworkStore n, PeopleStore p) : base(n, p, 19) { } }
    public sealed class PersonNetworkSlot20 : PersonNetworkUrlProvider { public PersonNetworkSlot20(PersonNetworkStore n, PeopleStore p) : base(n, p, 20) { } }
    public sealed class PersonNetworkSlot21 : PersonNetworkUrlProvider { public PersonNetworkSlot21(PersonNetworkStore n, PeopleStore p) : base(n, p, 21) { } }
    public sealed class PersonNetworkSlot22 : PersonNetworkUrlProvider { public PersonNetworkSlot22(PersonNetworkStore n, PeopleStore p) : base(n, p, 22) { } }
    public sealed class PersonNetworkSlot23 : PersonNetworkUrlProvider { public PersonNetworkSlot23(PersonNetworkStore n, PeopleStore p) : base(n, p, 23) { } }
}
