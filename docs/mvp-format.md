# The `.mvp` playlist format

The playlist format Multi-Video Player reads and writes. It is deliberately plain: a text
file you can read, diff and edit by hand.

## Summary

```
#MVP
D:\clips\atrium.mov
D:\clips\bridge.mov
\\nas\share\movies\madrid palace.mov
#FAV 1 45000 D:\clips\bridge.mov
```

- Extension: `.mvp`
- Line 1 is the signature `#MVP`
- Every other line is either an **entry** (a path) or a **directive** (starts with `#`)
- CRLF line endings, including after the final line
- UTF-8, no byte order mark
- No quoting and no escaping: a path runs to the end of its line, spaces and all
- Playback order follows file order

## Grammar

```abnf
playlist   = signature CRLF *( line CRLF )
signature  = %x23.4D.56.50              ; "#MVP"
line       = entry / directive
entry      = %x21-FF *( %x20-FF )       ; a path, not starting with "#", no CR or LF
directive  = "#" 1*( %x20-FF )
```

There is no terminator record; the file ends after the last line's CRLF.

## Entries

- **Absolute paths.** Relative paths are not resolved against anything meaningful.
- **UNC works** — `\\nas\share\clip.mov` is used verbatim.
- **Spaces are literal.** No quoting, no `%20`, no escape character.
- **One path per line**, nothing else on the line.

Because there is no escaping, a path containing CR or LF cannot be represented. Windows
forbids both in filenames, so this is unreachable in practice.

A line starting with `#` is never an entry. A path that genuinely begins with `#` cannot
be stored — an acceptable trade for being able to extend the format.

## Directives

A directive is any line beginning with `#`. **Unknown directives are ignored**, so a file
written by a later version still loads.

### `#FAV` — a favorite

```
#FAV <slot> <position-ms> <path>
```

| Field | Meaning |
| --- | --- |
| `slot` | 0–9, the number-row key that recalls it |
| `position-ms` | where in the video the favorite was marked, in milliseconds |
| `path` | the video, taken whole to the end of line so spaces need no quoting |

Written after the last entry. A slot appears at most once; a repeated slot takes the last
one seen. A `#FAV` naming a path that is not in the playlist still loads — recalling it
opens that file as the primary without adding it to the list.

Favorites live in the playlist rather than in application settings, so a playlist carries
its own marks and opening it elsewhere brings them along.

## Encoding

Written as **UTF-8 with no BOM**. Reading is deliberately more forgiving than writing:

- a leading BOM is stripped if present
- bare LF endings are accepted as well as CRLF
- blank lines are ignored
- unknown `#` directives are ignored
- the signature is matched case-insensitively

## Writing one

```csharp
var text = MvpPlaylist.Build(paths, favorites);
MvpPlaylist.Save("evening.mvp", paths, favorites);
```

By hand, in any language, the whole format is a header plus joined lines. The only two
things easy to get wrong are the line ending and the trailing newline:

```python
def build_mvp(paths):
    return "\r\n".join(["#MVP", *paths]) + "\r\n"

# newline="" stops Python translating \r\n into \r\r\n on Windows
with open("evening.mvp", "w", encoding="utf-8", newline="") as fh:
    fh.write(build_mvp(paths))
```

```powershell
$text = ((@("#MVP") + $paths) -join "`r`n") + "`r`n"
# UTF8Encoding($false) omits the BOM; Out-File and Set-Content may add one
[System.IO.File]::WriteAllText($target, $text, (New-Object System.Text.UTF8Encoding($false)))
```

## Reading one

```csharp
var document = MvpPlaylist.Load("evening.mvp");
document.Paths;      // the entries
document.Favorites;  // the #FAV records
```

```python
def parse_mvp(text):
    lines = text.lstrip("\ufeff").split("\n")
    if lines[0].strip().upper() != "#MVP":
        raise ValueError("not a playlist")
    return [ln.strip() for ln in lines[1:]
            if ln.strip() and not ln.strip().startswith("#")]
```

## Pitfalls

**Browser downloads rename the file.** If you generate `.mvp` from a web page, serve it as
`application/octet-stream`. With `text/plain`, Chrome rewrites the extension to match the
MIME type and you get `playlist.txt`.

**Language runtimes rewrite newlines.** Python text mode on Windows turns `\n` into `\r\n`;
combined with an explicit `\r\n` you get `\r\r\n`. Open with `newline=""`, or write bytes.

**BOM-adding helpers.** PowerShell's `Out-File` and `Set-Content`, and several editors, add
a UTF-8 BOM. The reader strips one, but do not add it deliberately.

**Paths belong to the machine that plays them.** If the paths come from a system with a
different view of the filesystem — a container, a NAS, a Linux host — rewrite them to what
the playing machine sees. A container path like `/data/movies/x.mov` means nothing to a
Windows player.
