# Yandex CardDAV Sync for Windows 10 Mobile

A UWP app for **Windows 10 Mobile (Lumia)** that syncs contacts between **Yandex** and the phone's People app via the CardDAV protocol.

Built with Visual Studio 2017, targeting Windows 10 Mobile (UAP 10.0.14393+).

---

## Features

- **Yandex → Phone** — downloads all your Yandex contacts into the People app
- **Phone → Yandex** — uploads contacts edited on the phone back to Yandex
- **Incremental sync** — on subsequent syncs only changed contacts are transferred, using ETag comparison
- **Auto sync** — background task runs every 15 minutes (Yandex → Phone direction)
- **Secure storage** — credentials stored in the Windows Credential Locker (PasswordVault), never in plain text
- **Full CardDAV discovery** — follows RFC 6352: principal → home → addressbook collection

---

## Screenshots

| Main screen | Sync in progress |
|---|---|
| *(add your screenshots here)* | *(add your screenshots here)* |

---

## Requirements

- Windows 10 Mobile (build 14393 or later) — tested on Lumia devices
- Visual Studio 2017 with UWP workload installed
- A Yandex account with an **App Password** (see setup below)

---

## Setup

### 1. Yandex App Password

The app uses Basic Auth with a Yandex **App Password** — do **not** use your regular account password.

1. Go to [id.yandex.ru/security](https://id.yandex.ru/security)
2. Enable **Two-Step Verification** if not already active
3. Scroll to **App passwords** → **Create app password**
4. Select type **Mail** (or **Other**), give it a name like "Lumia"
5. Copy the generated 16-character password

### 2. Build

1. Clone or download this repository
2. Open `YandexCardDAVSync.sln` in Visual Studio 2017
3. Right-click solution → **Restore NuGet Packages**
4. Install the signing certificate:
   - Double-click `YandexCardDAVSync\YandexCardDAVSync_TemporaryKey.pfx`
   - Install to **Current User → Personal** store, no password
5. Set platform to **ARM** (real device) or **x86** (emulator)
6. **Build → Deploy** to your phone

### 3. First run

1. Enter your Yandex address (e.g. `user@yandex.ru`)
2. Enter your 16-character App Password
3. Check **Remember credentials** if you want auto sync
4. Tap **Yandex → Phone** — this performs a full sync
5. Subsequent taps do incremental sync (only changes)

---

## How it works

### CardDAV discovery

The app follows the full RFC 6352 discovery chain, mirroring what a standard CardDAV client does:

```
PROPFIND carddav.yandex.ru/          →  current-user-principal
PROPFIND /principals/users/{user}/   →  addressbook-home-set
PROPFIND /addressbook/{user}/        →  find sub-collection with <addressbook/> resourcetype
PROPFIND /addressbook/{user}/1/      →  list contact hrefs + ETags  (Depth: 1)
GET      /addressbook/{user}/1/YA-N  →  download individual vCard
```

### Incremental sync (Yandex → Phone)

After the first full sync, ETags for all contacts are stored locally. On subsequent syncs:
- Server ETags are fetched and compared with stored ones
- Only contacts with changed or new ETags are downloaded
- Contacts missing from the server are deleted from the phone

### Change detection (Phone → Yandex)

After each Yandex → Phone sync, a hash of each contact's key fields is stored. When Phone → Yandex runs:
- Each contact's current hash is compared against the stored one
- Only changed contacts are uploaded via CardDAV PUT
- New contacts (no RemoteId) are created with a generated UID

### Background auto sync

A Windows Runtime Component (`SyncComponent`) registers a `TimeTrigger` background task that fires every 15 minutes. It performs an incremental Yandex → Phone sync using the same ETag logic as the foreground sync. The last sync time is shown in the app.

---

## Project structure

```
YandexCardDAVSync.sln
├── YandexCardDAVSync/                  ← Main UWP app (C#/XAML)
│   ├── MainPage.xaml / .cs             ← UI and sync button handlers
│   ├── Package.appxmanifest            ← App identity, capabilities
│   ├── Assets/                         ← App icons and splash screen
│   ├── Models/
│   │   └── VCardContact.cs             ← Data model for parsed vCards
│   ├── Services/
│   │   ├── CardDavService.cs           ← CardDAV HTTP client + Yandex discovery
│   │   ├── ContactStoreService.cs      ← W10M People app read/write
│   │   ├── VCardParser.cs              ← vCard 3.0 → VCardContact
│   │   └── VCardSerializer.cs          ← Contact → vCard 3.0 for upload
│   └── Helpers/
│       ├── BackgroundTaskHelper.cs     ← Register/unregister background task
│       ├── ContactHashStorage.cs       ← Hash-based change detection
│       ├── CredentialStorage.cs        ← PasswordVault wrapper
│       ├── ETagStorage.cs              ← ETag + uid→href persistence
│       └── LabelStorage.cs             ← Phone/email type label round-tripping
└── SyncComponent/                      ← Windows Runtime Component
    └── YandexToPhoneSyncTask.cs        ← Background task (self-contained)
```

---

## Limitations

- **Auto sync is Yandex → Phone only.** Phone → Yandex runs on manual tap only — Windows 10 Mobile does not provide a contact-change trigger for background tasks.
- **One addressbook.** The app syncs the default "Personal" addressbook (`/addressbook/{user}/1/`). Multiple addressbooks are not supported.
- **No photo sync.** Contact photos are not downloaded or uploaded.
- **App Password required.** Yandex OAuth is not implemented; Basic Auth with an App Password is used instead.
- **Certificate expiry.** The included signing certificate expires 3 April 2027. After that date, a new certificate must be generated to rebuild the appx.

---

## Signing certificate

The repository includes `YandexCardDAVSync_TemporaryKey.pfx` — a self-signed code-signing certificate generated for local development and sideloading. It is **not** a Store certificate.

To regenerate it if needed:

```bash
openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem \
  -days 365 -nodes \
  -subj "/CN=YandexCardDAVSync" \
  -addext "basicConstraints=critical,CA:FALSE" \
  -addext "extendedKeyUsage=critical,codeSigning"

openssl pkcs12 -export \
  -out YandexCardDAVSync_TemporaryKey.pfx \
  -inkey key.pem -in cert.pem -passout pass:
```

Then update `PackageCertificateThumbprint` in `YandexCardDAVSync.csproj` with the new SHA-1 thumbprint.

---

## Based on

This project is a port of [GmailCardDAVSync](https://github.com/) — a Gmail ↔ W10M contacts sync app — adapted for Yandex CardDAV.

Key differences from the Gmail version:

| | GmailCardDAVSync | YandexCardDAVSync |
|---|---|---|
| Server | `www.google.com` | `carddav.yandex.ru` |
| CardDAV path | Fixed, well-known | Discovered dynamically via RFC 6352 |
| Contact hrefs | `uid.vcf` format | `YA-N` format (no extension) |
| Auth hint | Google App Password | Yandex App Password |
| Contact list name | Gmail (CardDAV) | Yandex (CardDAV) |
| XML parsing | Hand-rolled string parser | `System.Xml.Linq` (XDocument) |

---

## License

MIT
