// Services/VCardSerializer.cs
// Converts a W10M Contact back into vCard 3.0 text for upload to Yandex.

using System;
using System.Text;
using Windows.ApplicationModel.Contacts;
using YandexCardDAVSync.Helpers;

namespace YandexCardDAVSync.Services
{
    public static class VCardSerializer
    {
        public static string Serialize(Contact c)
        {
            var sb = new StringBuilder();

            sb.AppendLine("BEGIN:VCARD");
            sb.AppendLine("VERSION:3.0");

            string uid = string.IsNullOrEmpty(c.RemoteId)
                ? Guid.NewGuid().ToString()
                : c.RemoteId;
            sb.AppendLine("UID:" + uid);

            // Full name
            string fn = c.DisplayName ?? string.Empty;
            if (string.IsNullOrEmpty(fn))
                fn = (c.FirstName + " " + c.LastName).Trim();
            if (!string.IsNullOrEmpty(fn))
                sb.AppendLine("FN:" + Escape(fn));

            // Structured name
            sb.AppendLine("N:" +
                Escape(c.LastName  ?? string.Empty) + ";" +
                Escape(c.FirstName ?? string.Empty) + ";;;");

            if (!string.IsNullOrEmpty(c.Nickname))
                sb.AppendLine("NICKNAME:" + Escape(c.Nickname));

            var savedLabels = LabelStorage.LoadLabels(uid);

            // Emails
            for (int i = 0; i < c.Emails.Count; i++)
            {
                var e = c.Emails[i];
                if (string.IsNullOrEmpty(e.Address)) continue;
                string type = GetEmailType(e, i, uid, savedLabels);
                sb.AppendLine("EMAIL;TYPE=" + type + ":" + e.Address);
            }

            // Phones
            for (int i = 0; i < c.Phones.Count; i++)
            {
                var p = c.Phones[i];
                if (string.IsNullOrEmpty(p.Number)) continue;
                string type = GetPhoneType(p, i, uid, savedLabels);
                sb.AppendLine("TEL;TYPE=" + type + ":" + p.Number);
            }

            // Addresses
            foreach (var a in c.Addresses)
            {
                string typeParam = AddressTypeParam(a.Kind);
                sb.AppendLine("ADR" + typeParam + ":;;" +
                    Escape(a.StreetAddress ?? string.Empty) + ";" +
                    Escape(a.Locality      ?? string.Empty) + ";" +
                    Escape(a.Region        ?? string.Empty) + ";" +
                    Escape(a.PostalCode    ?? string.Empty) + ";" +
                    Escape(a.Country       ?? string.Empty));
            }

            // Job info
            if (c.JobInfo.Count > 0)
            {
                if (!string.IsNullOrEmpty(c.JobInfo[0].CompanyName))
                    sb.AppendLine("ORG:" + Escape(c.JobInfo[0].CompanyName));
                if (!string.IsNullOrEmpty(c.JobInfo[0].Title))
                    sb.AppendLine("TITLE:" + Escape(c.JobInfo[0].Title));
            }

            // Websites
            foreach (var w in c.Websites)
                if (w.Uri != null)
                    sb.AppendLine("URL:" + w.Uri.ToString());

            // Birthday
            foreach (var date in c.ImportantDates)
            {
                if (date.Kind == ContactDateKind.Birthday && date.Year.HasValue)
                {
                    sb.AppendLine(string.Format("BDAY:{0:D4}{1:D2}{2:D2}",
                        date.Year.Value, (int)date.Month, (int)date.Day));
                    break;
                }
            }

            if (!string.IsNullOrEmpty(c.Notes))
                sb.AppendLine("NOTE:" + Escape(c.Notes));

            sb.AppendLine("END:VCARD");
            return sb.ToString();
        }

        private static string GetEmailType(ContactEmail e, int index,
            string uid, System.Collections.Generic.Dictionary<string, string> saved)
        {
            if (!string.IsNullOrEmpty(e.Description))
            {
                string t = NormalizeEmailType(e.Description);
                if (t != null) return t;
            }

            string savedKey = "email_" + index;
            if (saved.ContainsKey(savedKey))
                return saved[savedKey];

            switch (e.Kind)
            {
                case ContactEmailKind.Work:     return "INTERNET,WORK";
                case ContactEmailKind.Personal: return "INTERNET,HOME";
                default:                        return "INTERNET";
            }
        }

        private static string GetPhoneType(ContactPhone p, int index,
            string uid, System.Collections.Generic.Dictionary<string, string> saved)
        {
            if (!string.IsNullOrEmpty(p.Description))
            {
                string t = NormalizePhoneType(p.Description);
                if (t != null) return t;
            }

            string savedKey = "phone_" + index;
            if (saved.ContainsKey(savedKey))
                return saved[savedKey];

            return "CELL,VOICE";
        }

        private static string NormalizeEmailType(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string t = s.ToLowerInvariant();
            if (t == "home" || t == "personal") return "INTERNET,HOME";
            if (t == "work")                    return "INTERNET,WORK";
            return null;
        }

        private static string NormalizePhoneType(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string t = s.ToLowerInvariant();
            if (t == "home")   return "HOME,VOICE";
            if (t == "work")   return "WORK,VOICE";
            if (t == "mobile") return "CELL,VOICE";
            if (t == "pager")  return "PAGER";
            if (t == "fax")    return "FAX";
            return null;
        }

        private static string AddressTypeParam(ContactAddressKind kind)
        {
            switch (kind)
            {
                case ContactAddressKind.Home: return ";TYPE=HOME";
                case ContactAddressKind.Work: return ";TYPE=WORK";
                default:                      return ";TYPE=OTHER";
            }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\")
                    .Replace(";",  "\\;")
                    .Replace(",",  "\\,")
                    .Replace("\n", "\\n")
                    .Replace("\r", "");
        }
    }
}
