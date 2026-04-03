// Services/CardDavService.cs
// Fetches Yandex contacts via CardDAV (Basic Auth / App Password).
// Discovery mirrors Python getYandexContacts.py:
//   1. PROPFIND / → current-user-principal
//   2. PROPFIND principal → addressbook-home-set
//   3. PROPFIND home (Depth:1) → find addressbook sub-collection
//   4. PROPFIND collection (Depth:1) → list hrefs + etags
//   5. GET each href → download vCard

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using YandexCardDAVSync.Models;
using Windows.ApplicationModel.Contacts;

namespace YandexCardDAVSync.Services
{
    public class FetchAllResult
    {
        public List<VCardContact>         Contacts { get; set; } = new List<VCardContact>();
        public Dictionary<string, string> Etags    { get; set; } = new Dictionary<string, string>();
    }

    public class SyncDiff
    {
        public List<string>               ChangedHrefs { get; set; } = new List<string>();
        public List<string>               DeletedHrefs { get; set; } = new List<string>();
        public Dictionary<string, string> ServerEtags  { get; set; } = new Dictionary<string, string>();
    }

    public class CardDavService
    {
        private const string BaseUrl = "https://carddav.yandex.ru";

        private readonly HttpClient  _http;
        private readonly VCardParser _parser;

        public CardDavService(string yandexAddress, string appPassword)
        {
            _parser = new VCardParser();

            string credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(yandexAddress + ":" + appPassword));

            _http = new HttpClient();
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("YandexCardDAVSync/1.0");
            _http.Timeout = TimeSpan.FromSeconds(60);
        }

        // ================================================================
        // FULL SYNC
        // ================================================================
        public async Task<FetchAllResult> FetchAllContactsAsync(
            IProgress<string> progress = null)
        {
            progress?.Report("Discovering Yandex CardDAV endpoint...");
            string collectionUrl = await DiscoverAddressBookAsync();

            progress?.Report("Fetching contact list from Yandex...");
            var serverEtags = await FetchServerEtagsAsync(collectionUrl);

            progress?.Report("Found " + serverEtags.Count + " contacts. Downloading...");

            var contacts = new List<VCardContact>();
            int fetched  = 0;

            foreach (var kv in serverEtags)
            {
                var contact = await FetchSingleContactAsync(kv.Key);
                if (contact != null)
                {
                    contact.Etag = kv.Value;
                    contact.Href = kv.Key;
                    contacts.Add(contact);
                    fetched++;
                    if (fetched % 10 == 0)
                        progress?.Report("Downloaded " + fetched +
                                         " of " + serverEtags.Count + "...");
                }
            }

            progress?.Report("Done. " + contacts.Count + " contacts downloaded.");
            return new FetchAllResult { Contacts = contacts, Etags = serverEtags };
        }

        // ================================================================
        // INCREMENTAL SYNC
        // ================================================================
        public async Task<SyncDiff> GetChangesAsync(
            Dictionary<string, string> localEtags,
            IProgress<string> progress = null)
        {
            progress?.Report("Checking for changes on Yandex...");
            string collectionUrl = await DiscoverAddressBookAsync();
            var serverEtags      = await FetchServerEtagsAsync(collectionUrl);
            var diff             = new SyncDiff { ServerEtags = serverEtags };

            foreach (var kv in serverEtags)
                if (!localEtags.ContainsKey(kv.Key) || localEtags[kv.Key] != kv.Value)
                    diff.ChangedHrefs.Add(kv.Key);

            foreach (var href in localEtags.Keys)
                if (!serverEtags.ContainsKey(href))
                    diff.DeletedHrefs.Add(href);

            progress?.Report(diff.ChangedHrefs.Count + " changed, " +
                             diff.DeletedHrefs.Count + " deleted.");
            return diff;
        }

        // ================================================================
        // FETCH CHANGED
        // ================================================================
        public async Task<List<VCardContact>> FetchContactsByHrefsAsync(
            List<string> hrefs,
            Dictionary<string, string> serverEtags,
            IProgress<string> progress = null)
        {
            var contacts = new List<VCardContact>();
            int fetched  = 0;

            foreach (string href in hrefs)
            {
                var contact = await FetchSingleContactAsync(href);
                if (contact != null)
                {
                    contact.Href = href;
                    if (serverEtags.ContainsKey(href))
                        contact.Etag = serverEtags[href];
                    contacts.Add(contact);
                    fetched++;
                    if (fetched % 5 == 0)
                        progress?.Report("Fetched " + fetched +
                                         " of " + hrefs.Count + "...");
                }
            }
            return contacts;
        }

        // ================================================================
        // UPLOAD
        // ================================================================
        public async Task<string> UploadContactAsync(
            Contact contact,
            IProgress<string> progress = null)
        {
            try
            {
                string uid = string.IsNullOrEmpty(contact.RemoteId)
                    ? Guid.NewGuid().ToString()
                    : contact.RemoteId;

                string savedHref = YandexCardDAVSync.Helpers.ETagStorage.GetHrefForUid(uid);

                string uploadUrl;
                if (savedHref != null)
                {
                    uploadUrl = ToAbsUrl(savedHref);
                }
                else
                {
                    string collectionUrl = await DiscoverAddressBookAsync();
                    uploadUrl = ToAbsUrl(collectionUrl.TrimEnd('/') + "/" + uid + ".vcf");
                }

                string vcard = VCardSerializer.Serialize(contact);
                var req = new HttpRequestMessage
                {
                    Method     = HttpMethod.Put,
                    RequestUri = new Uri(uploadUrl),
                    Content    = new StringContent(vcard, Encoding.UTF8, "text/vcard")
                };
                if (savedHref == null)
                    req.Headers.TryAddWithoutValidation("If-None-Match", "*");

                var response = await _http.SendAsync(req);
                int code     = (int)response.StatusCode;
                bool ok      = response.IsSuccessStatusCode || code == 201 || code == 204;

                if (ok)
                {
                    if (savedHref == null)
                    {
                        string collectionUrl = await DiscoverAddressBookAsync();
                        string href = collectionUrl.TrimEnd('/') + "/" + uid + ".vcf";
                        var hrefs   = YandexCardDAVSync.Helpers.ETagStorage.LoadUidHrefs();
                        hrefs[uid]  = href;
                        YandexCardDAVSync.Helpers.ETagStorage.SaveUidHrefs(hrefs);
                    }
                    return uid;
                }
                return null;
            }
            catch { return null; }
        }

        // ================================================================
        // DISCOVERY — XLinq based, mirrors Python getYandexContacts.py
        // ================================================================
        private async Task<string> DiscoverAddressBookAsync()
        {
            // Step 1: current-user-principal
            string body1 =
                "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\">" +
                "<d:prop><d:current-user-principal/></d:prop></d:propfind>";
            string xml1 = await PropfindAsync(BaseUrl + "/", body1, "0");
            string principal = XmlGetHrefAfter(xml1, "current-user-principal");
            if (string.IsNullOrEmpty(principal))
                throw new Exception(
                    "Discovery failed: could not get current-user-principal.\n" +
                    "Check your Yandex App Password.");

            // Step 2: addressbook-home-set
            string body2 =
                "<?xml version=\"1.0\"?>" +
                "<d:propfind xmlns:d=\"DAV:\" xmlns:card=\"urn:ietf:params:xml:ns:carddav\">" +
                "<d:prop><card:addressbook-home-set/></d:prop></d:propfind>";
            string xml2 = await PropfindAsync(ToAbsUrl(principal), body2, "0");
            string home = XmlGetHrefAfter(xml2, "addressbook-home-set");
            if (string.IsNullOrEmpty(home))
                throw new Exception("Discovery failed: could not get addressbook-home-set.");

            // Step 3: find addressbook sub-collection (e.g. /addressbook/user/1/)
            string body3 =
                "<?xml version=\"1.0\"?>" +
                "<d:propfind xmlns:d=\"DAV:\" xmlns:card=\"urn:ietf:params:xml:ns:carddav\">" +
                "<d:prop><d:displayname/><d:resourcetype/></d:prop></d:propfind>";
            string xml3 = await PropfindAsync(ToAbsUrl(home), body3, "1");
            string collection = XmlFindAddressBook(xml3, home);
            return string.IsNullOrEmpty(collection) ? home : collection;
        }

        // Find first <href> value inside element with given local name
        private string XmlGetHrefAfter(string xml, string parentLocalName)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            try
            {
                var doc = XDocument.Parse(xml);
                foreach (var el in doc.Descendants())
                    if (el.Name.LocalName.Equals(parentLocalName,
                        StringComparison.OrdinalIgnoreCase))
                        foreach (var child in el.Descendants())
                            if (child.Name.LocalName.Equals("href",
                                StringComparison.OrdinalIgnoreCase))
                                return child.Value.Trim();
            }
            catch { }
            return null;
        }

        // Find the response whose resourcetype contains an "addressbook" element,
        // skipping the home collection itself
        private string XmlFindAddressBook(string xml, string homePath)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            string normalizedHome = homePath.TrimEnd('/');
            try
            {
                var doc = XDocument.Parse(xml);
                foreach (var response in doc.Descendants())
                {
                    if (!response.Name.LocalName.Equals("response",
                        StringComparison.OrdinalIgnoreCase)) continue;

                    string href = null;
                    foreach (var child in response.Descendants())
                        if (child.Name.LocalName.Equals("href",
                            StringComparison.OrdinalIgnoreCase))
                        { href = child.Value.Trim(); break; }

                    if (string.IsNullOrEmpty(href) ||
                        href.TrimEnd('/') == normalizedHome) continue;

                    foreach (var child in response.Descendants())
                        if (child.Name.LocalName.Equals("resourcetype",
                            StringComparison.OrdinalIgnoreCase))
                            foreach (var rt in child.Descendants())
                                if (rt.Name.LocalName.Equals("addressbook",
                                    StringComparison.OrdinalIgnoreCase))
                                    return href;
                }
            }
            catch { }
            return null;
        }

        // ================================================================
        // HTTP helpers
        // ================================================================
        private async Task<string> PropfindAsync(string url, string body, string depth)
        {
            try
            {
                var req = new HttpRequestMessage
                {
                    Method     = new HttpMethod("PROPFIND"),
                    RequestUri = new Uri(url),
                    Content    = new StringContent(body, Encoding.UTF8, "application/xml")
                };
                req.Headers.Add("Depth", depth);
                var response = await _http.SendAsync(req);

                if (!response.IsSuccessStatusCode)
                {
                    int code = (int)response.StatusCode;
                    if (code == 401)
                        throw new Exception(
                            "Authentication failed (401).\n" +
                            "Use a Yandex App Password from id.yandex.ru/security\n" +
                            "NOT your regular Yandex password.");
                    if (code == 403)
                        throw new Exception(
                            "Access denied (403).\n" +
                            "Ensure CardDAV access is enabled in Yandex settings.");
                    throw new Exception("Server error " + code + " " +
                        response.ReasonPhrase);
                }
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex) when (
                !ex.Message.StartsWith("Authentication") &&
                !ex.Message.StartsWith("Access denied") &&
                !ex.Message.StartsWith("Server error"))
            {
                throw new Exception("Network error: " + ex.Message, ex);
            }
        }

        private async Task<Dictionary<string, string>> FetchServerEtagsAsync(
            string collectionPath)
        {
            string body =
                "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\">" +
                "<d:prop><d:getetag/></d:prop></d:propfind>";

            string xml = await PropfindAsync(ToAbsUrl(collectionPath), body, "1");
            return ParseHrefEtagPairs(xml, collectionPath);
        }

        private Dictionary<string, string> ParseHrefEtagPairs(
            string xml, string collectionPath)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(xml)) return result;
            string normalizedColl = collectionPath.TrimEnd('/');
            try
            {
                var doc = XDocument.Parse(xml);
                foreach (var response in doc.Descendants())
                {
                    if (!response.Name.LocalName.Equals("response",
                        StringComparison.OrdinalIgnoreCase)) continue;

                    string href = null, etag = null;
                    foreach (var child in response.Descendants())
                    {
                        string ln = child.Name.LocalName;
                        if (ln.Equals("href",    StringComparison.OrdinalIgnoreCase) && href == null)
                            href = child.Value.Trim();
                        if (ln.Equals("getetag", StringComparison.OrdinalIgnoreCase))
                            etag = child.Value.Trim().Trim('"');
                    }

                    if (string.IsNullOrEmpty(href)) continue;
                    if (href.TrimEnd('/') == normalizedColl ||
                        href.TrimEnd('/') == normalizedColl.Replace(BaseUrl, "")) continue;

                    result[href] = etag ?? "";
                }
            }
            catch { }
            return result;
        }

        private async Task<VCardContact> FetchSingleContactAsync(string href)
        {
            try
            {
                var response = await _http.GetAsync(ToAbsUrl(href));
                if (!response.IsSuccessStatusCode) return null;
                string vcardText = await response.Content.ReadAsStringAsync();
                if (vcardText.IndexOf("BEGIN:VCARD",
                    StringComparison.OrdinalIgnoreCase) < 0) return null;
                var parsed = _parser.ParseMultiple(vcardText);
                return parsed.Count > 0 ? parsed[0] : null;
            }
            catch { return null; }
        }

        private string ToAbsUrl(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? path : BaseUrl + path;
        }
    }
}
