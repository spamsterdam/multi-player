# Multi-Video Player

A native Windows player for watching many videos at once. One video is *primary*; the
rest sit in numbered slots laid out like a 10-key. Press a digit and that video swaps
into the primary position — instantly, with no interruption to either video.

Two screen modes:

| Mode | Primary | Numbered slots | Keys |
| --- | --- | --- | --- |
| **Single screen** | left of one window | 6 tiles, right of the same window | keypad `7 9 4 6 1 3` |
| **Multi-screen** | fills the control display | 9 tiles filling a second display, with their transport | keypad `1`–`9` |

In multi-screen mode the numbered wall is a borderless full-display 3×3 grid arranged
exactly like a numeric keypad — `7 8 9` on the top row, `1 2 3` on the bottom — so the
key you press is in the position your fingers expect.

**The wall carries the entire control surface**: playlist, header, both transports,
favorites and legend all move there, so the primary display shows nothing but picture and
a single fullscreen control. That control surface is *moved*, not copied: the same WPF
elements are detached from one window and reattached to the other, so only one of them
ever exists and two copies cannot drift apart.

---

## Download

Built copies are on the [releases page](../../releases), in two flavours:

| | |
| --- | --- |
| `multi-player-<version>-win-x64-self-contained.zip` | Runs on a clean Windows machine. Nothing to install. Larger. |
| `multi-player-<version>-win-x64.zip` | Smaller, but needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). |

Unzip and run `MultiPlayer.exe`. The binaries are unsigned, so SmartScreen will warn on
first run. Each release also carries `SHA256SUMS.txt` and the corresponding source for the
LibVLC components — see [Licence](#licence).

## Requirements

- Windows 10/11, x64
- [.NET SDK 10.0](https://dotnet.microsoft.com/download) or newer to build
  (the project targets `net10.0-windows`; the runtime is not needed separately if you
  publish self-contained)
- Nothing else — LibVLC ships with the app via NuGet

## Build and run

```powershell
dotnet run --project src\MultiPlayer
```

The sample clips are generated rather than committed, since they are 58 MB of test
pattern. To get a playlist to open:

```powershell
.\tools\make-samples.ps1              # needs ffmpeg on PATH
dotnet run --project src\MultiPlayer -- .\samples\samples.mvp
```

A standalone build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
# output: src\MultiPlayer\bin\Release\net10.0-windows\win-x64\publish\
```

The `libvlc\win-x64` folder must stay next to `MultiPlayer.exe`.

**Why that folder exists.** LibVLC is not one library: it is `libvlccore.dll` plus **367
plugin DLLs, 131 MB of them**, which it loads from disk at runtime by scanning a `plugins`
directory. Codecs alone account for 46 MB. None of it can be linked into a managed
assembly, because .NET never loads those files — libvlc does, by path. Shipping it as a
folder is also what keeps the library replaceable, which the licence it carries depends on
— see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Trimming does not help: `PublishTrimmed` would strip types LibVLCSharp reaches by
reflection, and it cannot touch the native plugins that are nearly all of the size.
Deleting whole plugin folders by hand does work if you know what your media needs — `gui`
(18.9 MB) and `access_output` + `stream_out` (8.8 MB) are dead weight for a player — but
that trades formats away for megabytes.

### Command line

```
MultiPlayer.exe video1.mp4 video2.mp4 …     these files become the playlist
MultiPlayer.exe playlist.mvp  [extra…]      open the playlist, add anything after it
MultiPlayer.exe folder [extra…]             open the folder, add anything after it
MultiPlayer.exe --no-hw                     force software decoding
```

So a group of files dropped onto the executable becomes a playlist.

Set `MULTIPLAYER_DEBUG=1` to write a trace to `%TEMP%\multiplayer.log`.

## Loading videos

**Add files…** (or `Insert`) appends to the playlist and leaves everything on screen
playing. It is also how you start from nothing — there is no need to open an existing
playlist just to get going. Files already in the playlist are skipped rather than
duplicated.

**Open playlist…** (or `O`) *replaces* the playlist. It takes:

- an **`.mvp` playlist** — see [`docs/mvp-format.md`](docs/mvp-format.md)
- a **folder** — every video in it, sorted by name
- **several files** — used in the order you picked them
- **one video file** — loads its whole folder, starting at that file

Dropping onto the window follows the same split: a dropped **playlist** opens, dropped
**media** is added to what is already loaded — replacing a running wall is rarely what a
drag means. Dropping anything onto an empty playlist starts it.

**Save as .mvp…** writes the playlist as it now stands, deletions included: a `#MVP`
signature, one absolute path per line, CRLF throughout including a trailing one, UTF-8
with no BOM. Reading is deliberately more forgiving than writing — a BOM, bare LF endings,
blank lines and unknown `#` directives are all accepted. Full spec:
[`docs/mvp-format.md`](docs/mvp-format.md).

Favorites are written into the same file (see below), so they travel with the playlist
they belong to.

### Removing entries

`Delete` then a keypad digit drops that numbered video from the playlist; the slot refills
from the current page so the wall never leaves a hole. `Delete` then a *number row* digit
clears that favorite instead. Save afterwards to keep the change.

### Sets

The numbered slots show one page of the playlist at a time; `↑`/`↓` (or PgUp/PgDn) page
through them, wrapping round at either end. `T` steps through auto-advance dwell times —
off, 60s, 30s, 10s and back to off — cycling to the next set on that beat. Hiding pauses
the cycle; revealing resumes it at the same dwell.

Paging while the numbered videos are paused still updates the wall: a slot that has never
played would show nothing at all, so each new video is started, positioned on the shared
clock, and then parked on that frame. A page never shows the video that is currently primary — that slot draws
the next entry instead, so no video is ever on the wall and in the primary at once.

Paging is careful about what is already running: a video that appears in both the old
and new page keeps playing untouched, and so do the six that survive when you switch
between the 6-tile and 9-tile layouts.

## Keyboard

Everything is reachable from the number pad and the left hand. The same keys work on
either window.

**The two digit rows do different jobs.** The keypad drives the numbered slots, matching
their on-screen 10-key layout. The number row addresses the ten favorites. That split is
what lets a favorite be recalled with a single keystroke without stealing a slot key.

| Key | Action |
| --- | --- |
| keypad `1`–`9` | swap that slot with the primary |
| number row `0`–`9` | bring favorite *n* up as primary, at its stored position |
| `` ` `` then a number row digit | store the primary and its position in that slot |
| `Delete` then a keypad digit | remove that video from the playlist |
| `Delete` then a number row digit | clear that favorite |
| `R` | shuffle the playlist and draw a fresh numbered set |
| `←` `→` | step to the next / previous slot and swap it in |
| `↑` `↓`, PgUp / PgDn | previous / next set |
| `A` `Z` `X` `C` | primary: restart · back · play/pause · forward |
| `K` `,` `.` `/` | all numbered: restart · back · play/pause · forward |
| `Shift` + a seek key | seek 30s instead of 10s |
| `Space` | play/pause primary (`Shift+Space` = all numbered) |
| `D` | toggle single / dual screen |
| `H` | strip the wall back to picture only (captions and transport away) |
| `M` | mute |
| `O` | open a playlist, replacing the current one |
| `Insert` | add files to the playlist |
| `F1` | show/hide the key legend |
| `T` | step the auto-advance dwell: off → 60s → 30s → 10s → off |
| `F` | fullscreen the primary display (multi-screen mode) |
| `Esc` | black out and minimise the app · cancels a pending favorite or removal first |

Audio follows the *primary role*, not a particular file: whatever is primary is the only
thing you hear, and the sound moves the moment you swap.

Silence is done by **deselecting each numbered video's audio track**, not with mute or
volume. Both of those act on the audio output and neither is dependable: LibVLC documents
`libvlc_audio_set_mute` as "does not always work", and in practice the output resets
volume to 100 when it finally opens and then ignores further changes — which leaves every
video audible at once. Track selection is a decoder-side choice, so it always takes, is
free to reverse, and leaves the picture untouched.

## Favorites

Ten slots on the number row, each holding a video **and the position it was marked at**.
Press `` ` `` then a digit to store whatever is primary right now; press that digit on its
own to bring it back, resuming at the stored position rather than wherever that video has
since drifted to.

**Favorites belong to the playlist, not to the machine.** They are read out of the `.mvp`
file when it is opened and written back into it when it is saved; nothing carries over
between runs on its own. Set some and you must save the playlist to keep them.

The bar along the bottom shows all ten slots — on the control screen in both modes, and on
the wall as well when it is up — each with the frame grabbed at the moment it was stored,
so it shows *what* is behind a digit rather than merely that something is.

### How they are stored

Favorites are `#FAV` directive lines, written after the last entry:

```
#MVP
D:\clips\one.mp4
D:\clips\two.mp4
#FAV 1 45000 D:\clips\two.mp4
```

`#FAV <slot> <position in ms> <path>` — the path runs to end of line, so spaces need no
quoting. Any `#` line is a directive and never an entry, which is what keeps these from
being read back as videos, and what lets a later version add directives without breaking
an older reader.

### The stored frames

The pictures are a cache, never the record — the timecode in the playlist is. They live in
a hidden `.multiplayer` folder beside the playlist, named after it, so they travel with the
media rather than living in a system store:

```
clips\evening.mvp
clips\.multiplayer\evening.fav-1.png
```

Any frame that is missing — a playlist opened on a machine that has never seen it, or a
cache that was deleted — is decoded again from its stored timecode in the background,
one file at a time, and appears when it is ready. Roughly 0.6s per favorite for local
media here.

Only *missing* frames are rebuilt. Doing it for every favorite on every launch would be
the wrong trade: playlists commonly point at UNC shares, where ten opens would cost
seconds and compete with the ten streams already decoding.

If the playlist's own folder cannot be written — a read-only share — the cache falls back
to `%LOCALAPPDATA%\.multiplayer\`.

Recall is cheap when it can be: if the favorite is already on a numbered slot it is
swapped in, keeping its decoder warm; otherwise the primary opens it and seeks.

## Shuffle

`R` reorders the playlist and draws a fresh random set into the numbered slots. **The
primary is left alone** — this changes what is on deck, not what is on screen. Videos that
happen to land in the new set again keep playing untouched rather than restarting.

## The shared clock

A video dealt into a numbered slot does not start at zero. It starts at
`clock mod duration` — where it would have been had it been looping since the app opened —
so paging to a new set drops you into videos already in flight rather than nine
simultaneous title cards.

The clock stops while the numbered videos are paused, so pausing the wall and paging later
does not make the next set jump forward. `,` and `/` move the clock along with the videos
they seek, so the next set is dealt in at the same offset, and `K` resets both the videos
and the clock to zero.

Positioning is a seek once the length is known, not LibVLC's `:start-time`. That option
belongs to the input item and is re-applied on every repeat, so a clip opened at 90s of
120s would loop 90–120 forever and never show the first 90 seconds.

## Hiding

`Esc`, or the **hide** button, pauses everything, blacks out the app's own windows, and
minimises. The desktop is left alone — whatever else is running simply shows through.

The windows are genuinely black rather than merely minimised, so a taskbar preview shows
black too: every video surface is parked at the same time, since a hosted video window
would otherwise paint straight over the curtain.

Coming back is deliberately not one keypress. Bring the app up from the taskbar — it comes
back still black — and press **Esc, Enter, Esc**. Any other key restarts the sequence, so a
hand brushing the keyboard cannot put the video back on screen. Playback then resumes
exactly as it was.

## How the swap works

Every video gets one decoder and one native child window, married for the lifetime of
the app. Slots in the layout are empty container windows. Swapping a video into the
primary position re-parents its window into the primary container — a single Win32
`SetParent` call. LibVLC is not told anything: nothing is stopped, reopened or seeked.

That is why *both* videos carry on from exactly where they were, including across
displays in multi-screen mode. A more obvious implementation — swapping which file each
player has loaded and seeking to match — would stall for a few hundred milliseconds each
time and drift.

Two consequences worth knowing:

- Captions sit **below** each picture, never over it. A hosted video window always paints
  on top of WPF, so an overlay would simply be invisible.
- Clicking the picture selects that video. LibVLC renders into its own child window and
  swallows the button message — mouse messages do not bubble — so the click is picked up
  from `WM_PARENTNOTIFY`, which Windows sends to the parent chain. The surface still
  refuses *activation*, so a click can never steal keyboard focus and silently break every
  shortcut.

## Performance

Decoding runs on the GPU (`--avcodec-hw=d3d11va`) and frames stay in GPU memory through
a Direct3D 11 output, so cost scales with decoder blocks rather than CPU and PCIe
bandwidth. Idle slots release their decoder entirely.

Measured on this machine — RTX 3070, 20 logical cores — with **10 simultaneous 720p
streams** in multi-screen mode: **4.3% CPU**, ~850 MB RAM.

Audio isolation is verified acoustically, not just by inspection: each generated clip
carries a sine tone at `220 + 40n` Hz, so a loopback capture run through a Goertzel
filter shows exactly which clips are audible. With ten streams playing, the primary's
tone measures ~1800× above every other clip's, and it moves to the new primary on a swap.

If a machine has no usable hardware decoder, run with `--no-hw`.

## Layout

```
src/MultiPlayer/
  Model/        VideoEntry, the .mvp reader/writer, the two key layouts
  Playback/     LibVLC engine, the decoder+surface pool, Win32 plumbing,
                and PlayerController — which video holds which role
  Views/        MainWindow (control screen), WallWindow (second screen),
                TileCell, the keyboard map and the message hook
tools/
  make-samples.ps1   generates numbered test clips + a playlist (needs ffmpeg)
samples/             12 generated clips with burned-in index and timecode
docs/
  mvp-format.md      the playlist format
licenses/            LGPL-2.1 and GPL-2.0, for the LibVLC dependency
```

The test clips burn in their own number and a running timecode, which is what makes a
swap checkable by eye: the timecode has to carry straight on rather than restart.

## Licence

**GPL-2.0-or-later** — see [LICENSE](LICENSE).

Video playback is **LibVLC** by VideoLAN, used through **LibVLCSharp**. Both are
LGPL-2.1-or-later, neither is modified, and both are loaded as ordinary DLLs so they can be
replaced. Nothing from VideoLAN is checked into this repository; the packages arrive from
NuGet at build time.

GPL-2.0-or-later is chosen because the plugin set that ships with libvlc includes plugins
that are themselves GPL — x264, a52, faad, libmpeg2, dvdnav and others. Licensing this way
means a binary release can carry the plugin set untouched, with nothing to prune and no
formats lost.

Details, and what to include with a binary release, are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Licence texts are in
[`licenses/`](licenses/).

## Limits

- The wall needs a second display. With one display attached, multi-screen mode still
  opens but the wall covers the control screen.
- The pool is 10 decoders — one primary plus a full 3×3 wall.
- Videos are not synchronised to each other. `K` restarts every numbered video together,
  which is usually close enough; there is no frame-accurate lock.
- Promoting a video to primary starts its audio decoder from the current position, so
  sound can take a moment to arrive where the picture does not.
- A playlist entry that fails to open is marked in the sidebar but does not stop the set
  from loading.
