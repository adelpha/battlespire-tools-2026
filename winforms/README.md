# Battlespire tools for Windows

WinForms ports of the Python tools in
[adelpha/battlespire-tools-2026](https://github.com/adelpha/battlespire-tools-2026),
targeting .NET Framework 4.0 with no third-party dependencies.
The formats were checked against the Ghidra decompilation of `GAME.EXE`
and every file in the shipped archives.

| Tool | Replaces | What it does |
|---|---|---|
| **BsaTool** (BSA Browser) | `bsatool.py`, `bsafuse` | List, filter and sort an archive; preview images, models, levels, text and sounds; extract one entry or all of them; save a listing |
| **BsiTool** (BSI Viewer) | `bsitool.py`, `bsi.py` | All images and frames in a BSI, game colours (HICL), HTBL shade levels, CMAP, animation, PNG export (frame, strip, all frames, or a whole BSA) |
| **ModelTool** (Model Viewer) | `3dtool.py` | Shaded 3D preview, face list with texture ids and UVs, OBJ (with UVs and normals) and PLY export, batch export of 3D.BSA |
| **Bs6Tool** (Level Viewer) | `bs6tool.py` | Chunk tree with decoded values, objects with model names resolved, top-down map of objects, flats and lights, XML and TSV export |
| **RscTool** (Text Viewer) | `misc/rsctool.py` | Every TEXT.RSC string with its real id, search, control codes, TSV export |
| **BsiThumbnail** (Explorer thumbnails) | new | Shows .BSI files as thumbnails in Windows Explorer (first frame, game colours) |

Every viewer can open a loose file or pick an entry straight out of a `.BSA`.
Files can also be dragged onto the window or passed on the command line,
and the BSA Browser's "Open in viewer" button hands entries to the other tools.

## Building

Run `build.bat` (uses the MSBuild that comes with .NET Framework 4), or open
`BattlespireTools.sln` in any Visual Studio from 2010 on. Output goes to `bin\Release`.

## BSI thumbnails in Explorer

`BsiThumbnail.dll` is a Windows thumbnail handler, so Explorer shows loose `.bsi`
files as pictures instead of blank icons. It draws the first frame of the first image
in the game's colours, keeps transparent pixels transparent, and enlarges small
textures by whole steps so they stay sharp.

To turn it on, run `BsiThumbnailSetup.exe` from `bin\Release` and answer Yes. It
registers the handler for your Windows account only, so it needs no administrator
rights. Run it again to update the handler or turn thumbnails off (or use `BsiThumbnailSetup /install` and
`/uninstall` from a script).

- Setup copies the handler to `%LOCALAPPDATA%\BattlespireTools` and registers that copy,
  so `bin\Release` can be moved, cleaned or rebuilt freely. After rebuilding the
  handler itself, run the setup again and choose Yes to update the installed copy.
- Explorer runs the handler in a separate `dllhost.exe` (COM Surrogate), which can keep
  the installed copy open for a while. Setup copes with that by installing under a new
  name and removing the old copy later.
- Explorer caches thumbnails. If old blank icons remain, use Disk Cleanup's
  "Thumbnails" option.
- Files inside `.BSA` archives are not shown; extract them with the BSA Browser first.

## Fixes compared with the Python tools

Each of these was confirmed against the game code (addresses are in `GAME.EXE`).

**BSA archives** (`BsaOpenArchive` 0x2d9af, 0x2dce8, 0x2df52)
- The compression flag is bit 0 of byte 13 of a named record (bsatool tested bytes 12-13 as a word, which happens to work on the shipped files).
- Numeric (0x200) records are a 32 bit id and a 32 bit size. bsatool read the high half of the id as a compression flag and would try to decompress any entry with an id above 65535.
- Entries whose names repeat are no longer overwritten when extracting; numeric entries get an extension guessed from their contents.

**BSI images** (`LoadBsiImage` 0x15607, frame decoder 0x15f1b)
- Pixel values are byte offsets into HICL, so colour = HICL[pixel / 2]. Every pixel in BSI.BSA is even, which is why the old tools "lost" values over 127.
- HICL colours are RGB555 shifted left one bit (bit 0 unused), fitted against an in-game screenshot. bsi.py had this right; bsitool.py read plain RGB555, which scrambles the colours.
- HTBL is 32 shade levels of the 128 colours (RGB555, level 31 brightest). CMAP is a 6 bit VGA palette shared by most images.
- Compression 1 (RLE stream) and 2 (skip/copy runs) were not supported.
- BHDR +16 is the animation delay in timer ticks (the game uses 80 when it is 0).
- Files with several images (IFHD…DATA repeated) show every image, not just the first.

**3D models** (loader 0x87184, converter 0x8767c)
- Points and normals are 24.8 fixed point (÷256); texture coordinates are 12.4 fixed point (÷16). 3dtool wrote raw integers and ignored UVs.
- Files older than v2.6 store face point offsets as index×4, newer ones as index×12. 3dtool always divided by 12.
- The face header's texture field is 32 bits at +2 (3dtool read 16).
- Animated models (frame count at 0x10, frame table at 0x14) are read; 3dtool ignored them.
- Models inside 3D.BSA can be opened directly (almost all of them are LZSS compressed).

**BS6 levels** (`LoadMap` 0x22ecc, 0x23c75)
- bs6tool ignored the path passed to `readBS6File` and dropped a zero-length chunk at the end of a group.
- OBJD/IDFI indexes into LFIL; the game reads 13 characters of each 260 byte LFIL record and adds `.3d` when there is no extension. The viewer resolves these names.

**TEXT.RSC** (0x3c08a, 0x3c3dc)
- rsctool.py did not run (`import sys` missing).
- The index is a 16 bit length then 6 byte records of (16 bit id, 32 bit offset) starting at byte 2. rsctool started at byte 4, which paired every text with the next record's id.
- A text runs from its offset to the 0xFE terminator, not to the next record (id 14 shares its text with id 100). 0xFF separates random variants; 0xF8–0xFD are formatting codes.
