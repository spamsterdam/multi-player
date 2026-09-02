# Third-party notices

Multi-Video Player plays video through **LibVLC**, which is not this project's work. This
file records what is used, under what terms, and what those terms require of anyone
redistributing a build.

## What is used

| Component | Version | Copyright | Licence |
| --- | --- | --- | --- |
| [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) | 3.8.2 | VideoLAN | LGPL-2.1-or-later |
| [VideoLAN.LibVLC.Windows](https://code.videolan.org/videolan/libvlc-nuget) (libvlc, libvlccore and plugins) | 3.0.20 | VideoLAN | LGPL-2.1-or-later, **plus GPL-2.0-or-later plugins — see below** |

Both come from NuGet at build time. Licence text is in [`licenses/`](licenses/):
[LGPL-2.1](licenses/LGPL-2.1.txt), [GPL-2.0](licenses/GPL-2.0.txt).

Source for both is public at <https://code.videolan.org/videolan/>. Neither is modified
here — this project only calls the published API.

## The source repository redistributes nothing

No VideoLAN binary is checked in. `MultiPlayer.csproj` names two NuGet packages and the
files arrive at build time, so cloning and reading this repository triggers no
redistribution and no obligation beyond attribution.

**Obligations attach when you publish a build.** Everything below is about that.

## LGPL-2.1: libvlc and LibVLCSharp

Both packages declare `LGPL-2.1-or-later`. Distributing a build of this app alongside them
is straightforward, because the app satisfies the LGPL's central requirement — that a
recipient can replace the library — by construction:

- `libvlc.dll` and `libvlccore.dll` are ordinary DLLs loaded at runtime, and the plugins
  are loaded from a directory by path. Swapping in a different build of LibVLC is a matter
  of replacing files.
- `LibVLCSharp.dll` is a separate managed assembly, referenced not merged.
- Nothing is statically linked and no LibVLC source is modified.

When you publish a build, include:

1. The licence text (`licenses/LGPL-2.1.txt`).
2. This notice, or an equivalent attribution to VideoLAN.
3. A pointer to the library source: <https://code.videolan.org/videolan/vlc>.

The single-file build (`-p:PublishProfile=SingleFile`) bundles those DLLs into the
executable and unpacks them at startup. They are still loaded dynamically as separate
files, so replacement remains possible in principle — but it is a good deal less obvious
than a folder of DLLs. **The folder build is the cleaner thing to publish**; keep the
single file for internal convenience.

## GPL plugins: read this before publishing binaries

The NuGet package declares LGPL, but the plugin set it installs includes plugins that are
themselves **GPL-2.0-or-later**, among them:

```
libx264_plugin.dll   libx265_plugin.dll   libx26410b_plugin.dll
liba52_plugin.dll    libfaad_plugin.dll   liblibmpeg2_plugin.dll
libpostproc_plugin.dll                    libdvdnav_plugin.dll
libdvdread_plugin.dll                     libsid_plugin.dll
libvcd_plugin.dll    libcdda_plugin.dll   libsvcdsub_plugin.dll
```

GPL is stricter than LGPL: it reaches the whole distributed work rather than just the
library. So **shipping the plugin directory as-is means the build you publish must be
distributed under GPL-compatible terms**, whatever licence this project's own source
carries.

**This project takes the first of the two available routes:** it is licensed
**GPL-2.0-or-later** (see [LICENSE](LICENSE)), which matches those plugins and lets a
release carry the plugin set untouched, with nothing to prune and no formats lost.

The alternative, if a future version needs a permissive licence, is to ship without the
GPL plugins and leave an LGPL-only distribution. Most of them are encoders or optical-disc
support that a player does not need — libavcodec covers ordinary playback — but dropping
them costs some formats, so it would need testing against real media first.

None of this affects the source repository. It affects release artifacts.

## On the Videolabs commercial licence

Videolabs sells a [commercial licence for LibVLCSharp](https://videolabs.io/store/libvlcsharp/).
**An open-source project that complies with the LGPL does not need it.** It exists for
cases where compliance is impractical: closed-source applications unwilling to permit
relinking, or platforms where static linking is forced (iOS, most notably). Neither applies
to a Windows desktop app that loads `libvlc.dll` dynamically and publishes its own source.

If this project ever ships closed-source, or the GPL plugins become unacceptable and
pruning them is not enough, that store page is the route to a different arrangement — with
Videolabs for the bindings, and VideoLAN for libvlc itself.

## Not legal advice

This is an engineer's reading of the package metadata and the licences involved, recorded
so the reasoning is inspectable. If the distribution matters commercially, have a lawyer
check it.
