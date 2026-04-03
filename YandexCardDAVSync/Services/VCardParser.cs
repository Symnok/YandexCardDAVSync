// Services/VCardParser.cs
using System;
using System.Collections.Generic;
using YandexCardDAVSync.Models;

namespace YandexCardDAVSync.Services
{
    public class VCardParser
    {
        public List<VCardContact> ParseMultiple(string rawData)
        {
            var results = new List<VCardContact>();
            if (string.IsNullOrWhiteSpace(rawData)) return results;

            // Unfold continuation lines (RFC 6350)
            rawData = rawData.Replace("\r\n ", "").Replace("\r\n\t", "")
                             .Replace("\n ",   "").Replace("\n\t",   "");

            int searchFrom = 0;
            while (true)
            {
                int begin = rawData.IndexOf("BEGIN:VCARD", searchFrom,
                                            StringComparison.OrdinalIgnoreCase);
                if (begin < 0) break;

                int end = rawData.IndexOf("END:VCARD", begin,
                                          StringComparison.OrdinalIgnoreCase);
                if (end < 0) break;

                end += "END:VCARD".Length;
                results.Add(ParseSingle(rawData.Substring(begin, end - begin)));
                searchFrom = end;
            }
            return results;
        }

        public VCardContact ParseSingle(string vCardBlock)
        {
            var contact = new VCardContact();
            var lines   = vCardBlock.Split(
                new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                int colonPos = line.IndexOf(':');
                if (colonPos < 0) continue;

                string propFull = line.Substring(0, colonPos).ToUpperInvariant();
                string value    = line.Substring(colonPos + 1).Trim();

                // Strip "itemN." prefix (Yandex may use Apple-style labels)
                string propBase = propFull.Split(';')[0];
                int dotIdx = propBase.IndexOf('.');
                string propName = dotIdx >= 0
                    ? propBase.Substring(dotIdx + 1)
                    : propBase;

                if (propName.StartsWith("X-ABLABEL") ||
                    propName.StartsWith("X-AB"))
                    continue;

                switch (propName)
                {
                    case "FN":
                        contact.DisplayName = Unescape(value);
                        break;

                    case "N":
                        ParseN(value, contact);
                        break;

                    case "EMAIL":
                    {
                        string emailAddr = Unescape(value);
                        if (!string.IsNullOrWhiteSpace(emailAddr))
                            contact.Emails.Add(new ContactEmail
                            {
                                Address = emailAddr,
                                Type    = ExtractParam(propFull, "TYPE", "home")
                            });
                        break;
                    }

                    case "TEL":
                    {
                        string phoneNum = Unescape(value);
                        if (!string.IsNullOrWhiteSpace(phoneNum))
                            contact.Phones.Add(new ContactPhone
                            {
                                Number = phoneNum,
                                Type   = ExtractParam(propFull, "TYPE", "mobile")
                            });
                        break;
                    }

                    case "ADR":
                        contact.Addresses.Add(ParseAdr(propFull, value));
                        break;

                    case "ORG":
                        contact.Organization = Unescape(value.Split(';')[0]);
                        break;

                    case "TITLE":
                        contact.JobTitle = Unescape(value);
                        break;

                    case "NOTE":
                        contact.Notes = Unescape(value);
                        break;

                    case "NICKNAME":
                        contact.Nickname = Unescape(value);
                        break;

                    case "BDAY":
                        contact.Birthday = value;
                        break;

                    case "PHOTO":
                        if (propFull.Contains("VALUE=URI") || value.StartsWith("http",
                            StringComparison.OrdinalIgnoreCase))
                            contact.PhotoUrl = value;
                        break;

                    case "UID":
                        contact.Uid = value;
                        break;

                    case "URL":
                        string url = Unescape(value);
                        if (!string.IsNullOrEmpty(url))
                            contact.Websites.Add(new ContactWebsite
                            {
                                Url  = url,
                                Type = ExtractParam(propFull, "TYPE", "other")
                            });
                        break;
                }
            }

            if (string.IsNullOrEmpty(contact.DisplayName))
                contact.DisplayName = (contact.FirstName + " " + contact.LastName).Trim();

            return contact;
        }

        private void ParseN(string value, VCardContact c)
        {
            var parts = value.Split(';');
            if (parts.Length > 0) c.LastName   = Unescape(parts[0]);
            if (parts.Length > 1) c.FirstName  = Unescape(parts[1]);
            if (parts.Length > 3) c.NamePrefix = Unescape(parts[3]);
            if (parts.Length > 4) c.NameSuffix = Unescape(parts[4]);
        }

        private ContactAddress ParseAdr(string propFull, string value)
        {
            var parts = value.Split(';');
            return new ContactAddress
            {
                Street     = parts.Length > 2 ? Unescape(parts[2]) : string.Empty,
                City       = parts.Length > 3 ? Unescape(parts[3]) : string.Empty,
                Region     = parts.Length > 4 ? Unescape(parts[4]) : string.Empty,
                PostalCode = parts.Length > 5 ? Unescape(parts[5]) : string.Empty,
                Country    = parts.Length > 6 ? Unescape(parts[6]) : string.Empty,
                Type       = ExtractParam(propFull, "TYPE", "other")
            };
        }

        private string ExtractParam(string propFull, string paramName, string defaultVal)
        {
            string search = paramName + "=";
            int idx = propFull.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return defaultVal;

            idx += search.Length;
            int end = propFull.IndexOf(';', idx);
            string raw = end < 0
                ? propFull.Substring(idx)
                : propFull.Substring(idx, end - idx);

            raw = raw.ToLowerInvariant().Trim();
            if (string.IsNullOrEmpty(raw)) return defaultVal;

            return NormalizeType(raw, defaultVal);
        }

        private string NormalizeType(string raw, string defaultVal)
        {
            string[] parts = raw.Split(',');
            foreach (string part in parts)
            {
                string p = part.Trim();
                if (p == "home")                  return "home";
                if (p == "work")                  return "work";
                if (p == "cell" || p == "mobile") return "mobile";
                if (p == "x-mobile")              return "mobile";
                if (p == "pager")                 return "pager";
                if (p == "fax" || p == "x-fax")  return "fax";
                if (p == "other")                 return "other";
            }
            return defaultVal;
        }

        private string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\n", "\n")
                    .Replace("\\N", "\n")
                    .Replace("\\,", ",")
                    .Replace("\\;", ";")
                    .Replace("\\\\", "\\");
        }
    }
}
