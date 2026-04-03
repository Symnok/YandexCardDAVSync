// SyncComponent/YandexToPhoneSyncTask.cs
// Background task — runs every 15 minutes.
// Performs incremental Yandex→Phone sync using saved ETags.
// Self-contained — no linked files from main project.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Contacts;
using Windows.Security.Credentials;
using Windows.Storage;

namespace SyncComponent
{
    public sealed class YandexToPhoneSyncTask : IBackgroundTask
    {
        private BackgroundTaskDeferral _deferral;

        private const string ETagContainer = "CardDAVETags";
        private const string HrefContainer = "CardDAVHrefs";
        private const string ListName      = "Yandex (CardDAV)";
        private const string CredResource  = "YandexCardDAVSync";
        private const string BaseUrl       = "https://carddav.yandex.ru";
        private const string LastBgSyncKey = "LastBgSync";

        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            _deferral = taskInstance.GetDeferral();
            try   { await DoSyncAsync(); }
            catch { }
            finally { _deferral.Complete(); }
        }

        // ================================================================
        // MAIN SYNC LOGIC
        // ================================================================
        private async Task DoSyncAsync()
        {
            string email, password;
            if (!LoadCredentials(out email, out password)) return;

            var localEtags = LoadEtags();
            if (localEtags.Count == 0) return;

            var http = BuildHttpClient(email, password);

            string collectionUrl;
            try { collectionUrl = await DiscoverAddressBookAsync(http); }
            catch { return; }

            var serverEtags = await FetchServerEtagsAsync(http, collectionUrl);
            if (serverEtags == null) return;

            var changedHrefs = new List<string>();
            var deletedHrefs = new List<string>();

            foreach (var kv in serverEtags)
            {
                if (!localEtags.ContainsKey(kv.Key) ||
                    localEtags[kv.Key] != kv.Value)
                    changedHrefs.Add(kv.Key);
            }
            foreach (var href in localEtags.Keys)
                if (!serverEtags.ContainsKey(href))
                    deletedHrefs.Add(href);

            if (changedHrefs.Count == 0 && deletedHrefs.Count == 0)
            {
                ApplicationData.Current.LocalSettings.Values[LastBgSyncKey] =
                    DateTime.Now.ToString("dd MMM yyyy HH:mm") + " (no changes)";
                return;
            }

            var store = await ContactManager.RequestStoreAsync(
                ContactStoreAccessType.AppContactsReadWrite);
            if (store == null) return;

            var list = await GetOrCreateListAsync(store);

            foreach (var href in changedHrefs)
            {
                string vcf = await FetchVCardAsync(http, href);
                if (string.IsNullOrEmpty(vcf)) continue;
                var vc = ParseVCard(vcf, href,
                    serverEtags.ContainsKey(href) ? serverEtags[href] : "");
                if (vc == null) continue;
                await UpsertContactAsync(list, vc);
            }

            foreach (var href in deletedHrefs)
            {
                string uid = UidFromHref(href);
                if (!string.IsNullOrEmpty(uid))
                    await DeleteContactByUidAsync(list, uid);
            }

            SaveEtags(serverEtags);

            var uidHrefs = LoadUidHrefs();
            foreach (var href in changedHrefs)
            {
                string uid = UidFromHref(href);
                if (!string.IsNullOrEmpty(uid)) uidHrefs[uid] = href;
            }
            SaveUidHrefs(uidHrefs);

            ApplicationData.Current.LocalSettings.Values[LastBgSyncKey] =
                DateTime.Now.ToString("dd MMM yyyy HH:mm");
        }

        // ================================================================
        // CARDDAV DISCOVERY — XLinq based
        // ================================================================
        private async Task<string> DiscoverAddressBookAsync(HttpClient http)
        {
            string body1 =
                "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\">" +
                "<d:prop><d:current-user-principal/></d:prop></d:propfind>";
            string xml1 = await PropfindAsync(http, BaseUrl + "/", body1, "0");
            string principal = XmlGetHrefAfter(xml1, "current-user-principal");
            if (string.IsNullOrEmpty(principal)) throw new Exception("No principal");

            string body2 =
                "<?xml version=\"1.0\"?>" +
                "<d:propfind xmlns:d=\"DAV:\" xmlns:card=\"urn:ietf:params:xml:ns:carddav\">" +
                "<d:prop><card:addressbook-home-set/></d:prop></d:propfind>";
            string xml2 = await PropfindAsync(http, ToAbsUrl(principal), body2, "0");
            string home = XmlGetHrefAfter(xml2, "addressbook-home-set");
            if (string.IsNullOrEmpty(home)) throw new Exception("No home");

            string body3 =
                "<?xml version=\"1.0\"?>" +
                "<d:propfind xmlns:d=\"DAV:\" xmlns:card=\"urn:ietf:params:xml:ns:carddav\">" +
                "<d:prop><d:displayname/><d:resourcetype/></d:prop></d:propfind>";
            string xml3 = await PropfindAsync(http, ToAbsUrl(home), body3, "1");
            string collection = XmlFindAddressBook(xml3, home);
            return string.IsNullOrEmpty(collection) ? home : collection;
        }

        private string XmlGetHrefAfter(string xml, string parentLocalName)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            try
            {
                var doc = XDocument.Parse(xml);
                foreach (var el in doc.Descendants())
                    if (el.Name.LocalName.Equals(parentLocalName, StringComparison.OrdinalIgnoreCase))
                        foreach (var child in el.Descendants())
                            if (child.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))
                                return child.Value.Trim();
            }
            catch { }
            return null;
        }

        private string XmlFindAddressBook(string xml, string homePath)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            string normalizedHome = homePath.TrimEnd('/');
            try
            {
                var doc = XDocument.Parse(xml);
                foreach (var response in doc.Descendants())
                {
                    if (!response.Name.LocalName.Equals("response", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string href = null;
                    foreach (var child in response.Descendants())
                        if (child.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))
                        { href = child.Value.Trim(); break; }
                    if (string.IsNullOrEmpty(href) || href.TrimEnd('/') == normalizedHome) continue;
                    foreach (var child in response.Descendants())
                        if (child.Name.LocalName.Equals("resourcetype", StringComparison.OrdinalIgnoreCase))
                            foreach (var rt in child.Descendants())
                                if (rt.Name.LocalName.Equals("addressbook", StringComparison.OrdinalIgnoreCase))
                                    return href;
                }
            }
            catch { }
            return null;
        }

        // ================================================================
        // CONTACT STORE
        // ================================================================
        private async Task<ContactList> GetOrCreateListAsync(ContactStore store)
        {
            var lists = await store.FindContactListsAsync();
            foreach (var l in lists)
                if (l.DisplayName == ListName) return l;
            var newList = await store.CreateContactListAsync(ListName);
            newList.OtherAppReadAccess  = ContactListOtherAppReadAccess.Full;
            newList.OtherAppWriteAccess = ContactListOtherAppWriteAccess.SystemOnly;
            await newList.SaveAsync();
            return newList;
        }

        private async Task UpsertContactAsync(ContactList list, ParsedContact vc)
        {
            var reader = list.GetContactReader();
            var batch  = await reader.ReadBatchAsync();
            while (batch.Contacts.Count > 0)
            {
                foreach (var c in batch.Contacts)
                    if (!string.IsNullOrEmpty(vc.Uid) && c.RemoteId == vc.Uid)
                    { await list.DeleteContactAsync(c); break; }
                batch = await reader.ReadBatchAsync();
            }
            await list.SaveContactAsync(ToUwpContact(vc));
        }

        private async Task DeleteContactByUidAsync(ContactList list, string uid)
        {
            var reader = list.GetContactReader();
            var batch  = await reader.ReadBatchAsync();
            while (batch.Contacts.Count > 0)
            {
                foreach (var c in batch.Contacts)
                    if (c.RemoteId == uid) { await list.DeleteContactAsync(c); return; }
                batch = await reader.ReadBatchAsync();
            }
        }

        private Contact ToUwpContact(ParsedContact vc)
        {
            var c = new Contact
            {
                FirstName  = vc.FirstName  ?? "",
                LastName   = vc.LastName   ?? "",
                MiddleName = "",
                Nickname   = vc.Nickname   ?? "",
                Notes      = vc.Notes      ?? "",
                RemoteId   = vc.Uid        ?? ""
            };
            foreach (var e in vc.Emails)
                c.Emails.Add(new ContactEmail
                {
                    Address = e.Address ?? "",
                    Kind    = e.IsWork ? ContactEmailKind.Work : ContactEmailKind.Personal
                });
            foreach (var p in vc.Phones)
                c.Phones.Add(new ContactPhone
                {
                    Number = p.Number ?? "", Description = p.Description ?? ""
                });
            if (!string.IsNullOrEmpty(vc.Org) || !string.IsNullOrEmpty(vc.Title))
                c.JobInfo.Add(new ContactJobInfo
                {
                    CompanyName = vc.Org ?? "", Title = vc.Title ?? ""
                });
            if (!string.IsNullOrEmpty(vc.Birthday))
            {
                DateTimeOffset bday;
                if (TryParseBirthday(vc.Birthday, out bday))
                    c.ImportantDates.Add(new ContactDate
                    {
                        Kind = ContactDateKind.Birthday,
                        Day = (uint)bday.Day, Month = (uint)bday.Month, Year = bday.Year
                    });
            }
            if (string.IsNullOrEmpty(c.FirstName) && string.IsNullOrEmpty(c.LastName))
            {
                if (!string.IsNullOrEmpty(vc.DisplayName))
                    c.Nickname = vc.DisplayName;
                else if (c.Emails.Count > 0)
                    c.Nickname = c.Emails[0].Address;
            }
            return c;
        }

        // ================================================================
        // CARDDAV HTTP
        // ================================================================
        private HttpClient BuildHttpClient(string email, string password)
        {
            string cred = Convert.ToBase64String(Encoding.UTF8.GetBytes(email + ":" + password));
            var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", cred);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("YandexCardDAVSync/1.0");
            http.Timeout = TimeSpan.FromSeconds(30);
            return http;
        }

        private async Task<string> PropfindAsync(
            HttpClient http, string url, string body, string depth)
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
                var resp = await http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync();
            }
            catch { return null; }
        }

        private async Task<Dictionary<string, string>> FetchServerEtagsAsync(
            HttpClient http, string collectionPath)
        {
            string body =
                "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\">" +
                "<d:prop><d:getetag/></d:prop></d:propfind>";
            string xml = await PropfindAsync(http, ToAbsUrl(collectionPath), body, "1");
            if (xml == null) return null;
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
                    if (!response.Name.LocalName.Equals("response", StringComparison.OrdinalIgnoreCase))
                        continue;
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

        private async Task<string> FetchVCardAsync(HttpClient http, string href)
        {
            try
            {
                var resp = await http.GetAsync(ToAbsUrl(href));
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync();
            }
            catch { return null; }
        }

        // ================================================================
        // VCARD PARSER
        // ================================================================
        private ParsedContact ParseVCard(string vcf, string href, string etag)
        {
            var vc = new ParsedContact { Href = href, Etag = etag };
            vcf = vcf.Replace("\r\n ", "").Replace("\r\n\t", "")
                     .Replace("\n ", "").Replace("\n\t", "");
            foreach (var rawLine in vcf.Split(new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string prop  = line.Substring(0, colon).ToUpperInvariant();
                string value = line.Substring(colon + 1).Trim();
                if (prop.StartsWith("ITEM") || prop.Contains("X-ABLABEL")) continue;
                string propName = prop.Contains(";") ? prop.Substring(0, prop.IndexOf(';')) : prop;
                int dotIdx = propName.IndexOf('.');
                if (dotIdx >= 0) propName = propName.Substring(dotIdx + 1);
                string typeParam = prop.Contains(";TYPE=")
                    ? prop.Substring(prop.IndexOf(";TYPE=") + 6).ToLowerInvariant() : "";
                switch (propName)
                {
                    case "UID":      vc.Uid         = value;                         break;
                    case "FN":       vc.DisplayName = Unescape(value);               break;
                    case "NICKNAME": vc.Nickname    = Unescape(value);               break;
                    case "NOTE":     vc.Notes       = Unescape(value);               break;
                    case "ORG":      vc.Org         = Unescape(value.Split(';')[0]); break;
                    case "TITLE":    vc.Title       = Unescape(value);               break;
                    case "BDAY":     vc.Birthday    = value;                         break;
                    case "N":
                        var parts = value.Split(';');
                        if (parts.Length > 0) vc.LastName  = Unescape(parts[0]);
                        if (parts.Length > 1) vc.FirstName = Unescape(parts[1]);
                        break;
                    case "EMAIL":
                        if (!string.IsNullOrWhiteSpace(value))
                            vc.Emails.Add(new ParsedEmail
                            { Address = value, IsWork = typeParam.Contains("work") });
                        break;
                    case "TEL":
                        if (!string.IsNullOrWhiteSpace(value))
                            vc.Phones.Add(new ParsedPhone
                            { Number = value, Description = PhoneTypeToDescription(typeParam) });
                        break;
                }
            }
            if (string.IsNullOrEmpty(vc.Uid)) vc.Uid = UidFromHref(href);
            return vc;
        }

        private string PhoneTypeToDescription(string type)
        {
            if (string.IsNullOrEmpty(type))  return "Mobile";
            if (type.Contains("home"))        return "Home";
            if (type.Contains("work"))        return "Work";
            if (type.Contains("cell") ||
                type.Contains("mobile"))      return "Mobile";
            if (type.Contains("pager"))       return "Pager";
            if (type.Contains("fax"))         return "Fax";
            return "Mobile";
        }

        private string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\n", "\n").Replace("\\N", "\n")
                    .Replace("\\,", ",").Replace("\\;", ";")
                    .Replace("\\\\", "\\");
        }

        private string UidFromHref(string href)
        {
            int slash = href.LastIndexOf('/');
            return slash >= 0 ? href.Substring(slash + 1) : href;
        }

        private bool TryParseBirthday(string s, out DateTimeOffset result)
        {
            result = DateTimeOffset.MinValue;
            s = s.Replace("-", "");
            if (s.Length < 8) return false;
            int y, m, d;
            if (!int.TryParse(s.Substring(0, 4), out y)) return false;
            if (!int.TryParse(s.Substring(4, 2), out m)) return false;
            if (!int.TryParse(s.Substring(6, 2), out d)) return false;
            try { result = new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero); return true; }
            catch { return false; }
        }

        private string ToAbsUrl(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? path : BaseUrl + path;
        }

        // ================================================================
        // CREDENTIALS
        // ================================================================
        private bool LoadCredentials(out string email, out string password)
        {
            email = password = null;
            try
            {
                var vault = new PasswordVault();
                var creds = vault.FindAllByResource(CredResource);
                if (creds == null || creds.Count == 0) return false;
                var cred = creds[0];
                cred.RetrievePassword();
                email = cred.UserName; password = cred.Password;
                return true;
            }
            catch { return false; }
        }

        // ================================================================
        // ETAG STORAGE
        // ================================================================
        private Dictionary<string, string> LoadEtags()
        {
            var result   = new Dictionary<string, string>();
            var settings = ApplicationData.Current.LocalSettings;
            if (!settings.Containers.ContainsKey(ETagContainer)) return result;
            foreach (var kv in settings.Containers[ETagContainer].Values)
            {
                string s = kv.Value as string;
                if (string.IsNullOrEmpty(s)) continue;
                int sep = s.IndexOf('|');
                if (sep >= 0) result[s.Substring(0, sep)] = s.Substring(sep + 1);
            }
            return result;
        }

        private void SaveEtags(Dictionary<string, string> etags)
        {
            var settings  = ApplicationData.Current.LocalSettings;
            var container = settings.CreateContainer(ETagContainer,
                ApplicationDataCreateDisposition.Always);
            container.Values.Clear();
            foreach (var kv in etags)
                container.Values[SafeKey(kv.Key)] = kv.Key + "|" + kv.Value;
        }

        private Dictionary<string, string> LoadUidHrefs()
        {
            var result   = new Dictionary<string, string>();
            var settings = ApplicationData.Current.LocalSettings;
            if (!settings.Containers.ContainsKey(HrefContainer)) return result;
            foreach (var kv in settings.Containers[HrefContainer].Values)
            {
                string s = kv.Value as string;
                if (string.IsNullOrEmpty(s)) continue;
                int sep = s.IndexOf('|');
                if (sep >= 0) result[s.Substring(0, sep)] = s.Substring(sep + 1);
            }
            return result;
        }

        private void SaveUidHrefs(Dictionary<string, string> hrefs)
        {
            var settings  = ApplicationData.Current.LocalSettings;
            var container = settings.CreateContainer(HrefContainer,
                ApplicationDataCreateDisposition.Always);
            container.Values.Clear();
            foreach (var kv in hrefs)
                if (!string.IsNullOrEmpty(kv.Key))
                    container.Values[SafeKey(kv.Key)] = kv.Key + "|" + kv.Value;
        }

        private string SafeKey(string s)
        {
            return s.Length <= 200 ? s : s.Substring(s.Length - 200);
        }

        // ================================================================
        // DATA CLASSES
        // ================================================================
        private class ParsedContact
        {
            public string Uid { get; set; } public string Href { get; set; }
            public string Etag { get; set; } public string DisplayName { get; set; }
            public string FirstName { get; set; } public string LastName { get; set; }
            public string Nickname { get; set; } public string Notes { get; set; }
            public string Org { get; set; } public string Title { get; set; }
            public string Birthday { get; set; }
            public List<ParsedEmail> Emails { get; set; } = new List<ParsedEmail>();
            public List<ParsedPhone> Phones { get; set; } = new List<ParsedPhone>();
        }
        private class ParsedEmail { public string Address { get; set; } public bool IsWork { get; set; } }
        private class ParsedPhone { public string Number { get; set; } public string Description { get; set; } }
    }
}
