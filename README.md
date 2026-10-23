# Nova Music

A local music player for Windows with a dark Material 3 inspired interface, built with Visual Basic and WPF on .NET 10.

## Run

Open `music player.slnx` in the parent folder with Visual Studio, or run from this directory:

```powershell
dotnet run --project "music player.vbproj"
```

Choose **Add music** to scan a folder and its subfolders. Nova reads titles, artists, albums, durations, and embedded cover art from your files. It recognizes MP3, WAV, FLAC, M4A, AAC, and WMA; playback uses the audio support installed in Windows.

## Your collection

- Search by song, artist, or album. Press **Ctrl+F** to focus search and **Esc** to clear it.
- Save songs using the heart, then find them in **Favourites**.
- Browse **New additions** to see files ordered by their modification date.
- Select a song to play the current collection. Searching or changing views keeps that playback queue intact.
- Toggle shuffle or cycle repeat through **off**, **queue**, and **song**.
- Use **Space** to play/pause when you are outside a text field or focused button. Slider arrow keys adjust position or volume; focused buttons support standard Space activation.
- Playback controls live in the listening card. Its queue button shows or hides upcoming songs; shorter windows automatically use a compact player.

Your folder, likes, volume, shuffle, repeat, and library view are saved in `%LOCALAPPDATA%\music-player\settings.json`. This player uses your local files and does not connect to Spotify or a streaming catalogue.

## Verification

```powershell
dotnet build "music player.vbproj" --no-restore
pwsh -NoProfile -STA -File .\tools\Render-Player.ps1 -DemoLibrary -RunChecks
```

The render script uses a separate fixture library and preferences under `artifacts/`. It generates previews without opening a visible application window.

