# Third-Party Notices

Release archives also contain a generated, RID-specific shipped-component inventory, package
license files supplied by dependencies where applicable, and clearly named .NET runtime license
and third-party-notice files. Build- and test-only dependencies are not shipped.

DinoRand is modelled on and derives concepts and data shapes from the following
project. Its license and copyright notice are reproduced here in accordance with
its terms.

## BioRand (classic)

- Source: https://github.com/biorand/classic
- License: MIT
- Copyright (c) 2022-2026 Ted John

```
MIT License

Copyright (c) 2022-2026 Ted John

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## biohazard-utils

- Source: https://github.com/IntelOrca/biohazard-utils
- License: MIT
- Copyright (c) 2022-2023 Ted John
- Used by `tools/DinoCrisis.Interchange` (DCM↔RE model interchange). The source is not
  redistributed here (`ref/` is gitignored); this notice covers derived use.

```
MIT License

Copyright (c) 2022-2023 Ted John

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Data attribution — Dino Crisis Fandom Wiki

Some rows in `data/dc1/room-data.json` (room names/metadata and a per-room `source_url`) are
derived from the Dino Crisis Fandom Wiki.

- Source: https://dinocrisis.fandom.com/
- License: Creative Commons Attribution-ShareAlike 3.0 — https://creativecommons.org/licenses/by-sa/3.0/
- These wiki-derived fields remain under **CC BY-SA 3.0**; the project's MIT [LICENSE](LICENSE) does
  not extend to them. The decoded-from-game fields (placed enemies, flags) are the project's own work.
