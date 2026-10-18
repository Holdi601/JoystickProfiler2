# JoyPro.WtOodle (War Thunder Oodle helper)

Unpacks the Oodle Leviathan (and Kraken) data of War Thunder's cockpit packs for the engine's War Thunder cockpit
pictures (`engine cockpits pictures build --game WarThunder`, docs/cockpits.md). It is only compression: no key, no
cipher, nothing obfuscated is read.

- Build: `dotnet build tools\wt-oodle -c Release` (net9.0). The engine finds
  `tools\wt-oodle\bin\Release\net9.0\JoyPro.WtOodle.exe` from its own folder upwards, or the path in `JOYPRO_WT_OODLE`.
- Protocol (`JoyPro.WtOodle serve`): the client writes `JPWO`, then requests (int32 packed length, int32 raw length,
  int32 stop-after, packed bytes); each answer is int32 status (0 ok), int32 length, bytes (data or error text).
  `JoyPro.WtOodle decode <file> <offset> <packedLength> <rawLength> <out>` unpacks one block.
- Checked on the maintainer's install (2026-10-18): all 25,764 Oodle-packed cockpit textures and all cockpit models
  unpack; where the original ooz also unpacks a block the output is identical.

## License

`OodleLz.cs` is a C# port of parts of ooz (Kraken Decompressor for Windows, Copyright (C) 2016 Powzix,
https://github.com/powzix/ooz), licensed under the GNU General Public License version 3 or later; this helper is
licensed the same way (GPL-3.0-or-later, the text is the repository's `LICENSE`). It is a separate program: the
engine only starts it as a process. It is a maintainer tool and not part of JoyPro's release package; the War Thunder
pictures reach users as picture packs.
