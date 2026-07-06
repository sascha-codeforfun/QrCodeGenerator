# QR Code Generator (WPF)

A small Windows desktop app that turns a URL (or any text) into a QR code and
exports it as **.png** (raster) or **.svg** (vector) — optionally with a
**center image / logo** of your choice.

## Requirements

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (any 8.x)

## Build & run

From the project folder:

```bash
dotnet run
```

Or build a release executable:

```bash
dotnet build -c Release
```

The `.exe` lands in `bin/Release/net8.0-windows/`.

## Usage

1. Type or paste a URL into the **URL / Text** field.
2. (Optional) adjust:
   - **Error correction** — higher levels survive more damage/obstruction.
   - **Pixels per module** — controls the PNG resolution (SVG is vector and always scales cleanly).
   - **Dark / light color** — 6-digit hex, e.g. `#000000` / `#FFFFFF`.
   - **Center image** — click **Browse…** to pick a PNG/JPG/BMP to place in the middle,
     and set **Logo size %** (5–40% of the QR width).
3. Click **Generate preview**.
4. Click **Save .png** or **Save .svg**.

## Center graphic (optional)

Pick a mode from the **Center graphic** dropdown on the Single tab:

- **None** — plain QR code.
- **Image file (PNG / JPG)** — a raster logo. Simple, but it is embedded as-is, so
  it can look soft when the SVG is scaled up and it enlarges the file.
- **Font glyph (vector, TTF / OTF)** — pick a font, type the character(s), and choose
  a color. The app extracts the glyph **outline** and composites it as vector: in the
  SVG it becomes a `<path>`, so it stays razor-sharp at any size; in the PNG it is
  rasterized crisply at the chosen resolution.

Common notes:

- The graphic sits on a white, rounded pad so it stays readable, and its size is set
  by **Graphic size (% of QR width)** (default 22%).
- **Use High (H) error correction with a center graphic.** It covers part of the code;
  H reserves ~30% redundancy so scanners can still read it. The app reminds you of this.
- **PNG** compositing uses WPF's imaging stack (`RenderTargetBitmap` /
  `PngBitmapEncoder`) — no `System.Drawing`/GDI+ dependency. The glyph outline is
  drawn with `DrawGeometry`.
- **SVG** embeds an image logo as a base64 `<image>`, or a font glyph as a true
  `<path>` (with a `matrix()` transform to position it) — both centered in the vector.

### Fonts: TTF/OTF only

WPF's glyph APIs read **TTF, OTF and TTC** files. They do **not** read **WOFF/WOFF2**,
which are web-compressed wrappers (zlib / Brotli). Convert a `.woff`/`.woff2` to
`.ttf` or `.otf` first (e.g. with an online converter or `fonttools`) and load that.

Characters that don't exist in the chosen font are skipped; whitespace-only input is
rejected. Simple BMP characters (letters, digits, most icon-font glyphs) work; multi-
codepoint emoji sequences are not supported.

## Library

QR generation uses [QRCoder](https://github.com/codebude/QRCoder). Both outputs come
from the same encoded data, so PNG and SVG are identical content.

## Batch tab (URLs → ZIP of SVGs)

Switch to the **Batch → ZIP** tab, paste one URL per line, and click **Generate ZIP…**.
Each URL becomes a QR `.svg` inside a single ZIP. The colors and error-correction
level from the **Single** tab are reused (no logo is applied in batch mode).

### Filename rules

Each SVG is named from its URL:

1. **Protocol dropped** — `https://` / `http://` is removed.
2. **Subdomain dropped** — only the registrable domain is kept
   (`www.blog.example.com/x` → `example.com/x`). Common two-part TLDs such as
   `co.uk` are handled so they aren't over-trimmed.
3. **Sanitized** — the result is lowercased and **every character that is not
   `a–z` or `0–9` (path and querystring included) becomes `_`**.
4. Leading/trailing underscores are trimmed, names longer than 120 chars are
   truncated, duplicate names get `_2`, `_3`, … , and `.svg` is appended.

Examples:

| URL | Filename |
|-----|----------|
| `https://www.example.com/products?id=42&ref=home` | `example_com_products_id_42_ref_home.svg` |
| `http://blog.shop.example.co.uk/a/b/`             | `example_co_uk_a_b.svg` |
| `https://api.github.com/repos/foo/bar`            | `github_com_repos_foo_bar.svg` |

The QR code itself always encodes the **full original URL** — only the filename is sanitized.

## Build a URL from parameters (Single tab)

The Single tab can assemble the URL for you from a JSON schema instead of typing it
by hand. The result follows:

```
DOMAIN/PREFIX?name1=value1&name2=value2…
```

- A sample `url-schema.json` ships next to the executable and is **auto-loaded on startup**.
- Click **Load schema…** to use a different file.
- Each parameter appears as a labeled input, pre-filled with its `default`. Edit the
  values, then click **Build URL ↓** to write the composed URL into the URL box
  (you can still hand-edit it afterwards).

### Schema format

```json
{
  "domain": "https://example.com",
  "prefix": "landing",
  "parameters": [
    { "name": "utm_source",   "label": "Source",   "default": "qr" },
    { "name": "utm_medium",   "label": "Medium",   "default": ["print", "email", "social", "web"] },
    { "name": "utm_campaign", "label": "Campaign", "default": "spring_sale" },
    { "name": "ref",          "label": "Referrer", "default": "", "omitIfEmpty": true }
  ]
}
```

| Field | Required | Meaning |
|-------|----------|---------|
| `domain` | yes | Base domain, including scheme (`https://example.com`). |
| `prefix` | no | Path after the domain (`landing`, `campaign/spring`). Leading/trailing slashes are handled. |
| `parameters[].name` | yes | The query key. |
| `parameters[].label` | no | Friendly label for the input; falls back to `name`. |
| `parameters[].default` | no | A **single value** renders an editable **textbox** pre-filled with it. A **list of values** renders a **dropdown** with the first entry pre-selected. |
| `parameters[].omitIfEmpty` | no | Defaults to `true` — a blank value is left out of the URL. Set `false` to always include it as `name=`. |

So `"default": "print"` gives a textbox, while `"default": ["print", "email", "social"]` gives a
dropdown that starts on `print`. A single-element list is treated as one value (textbox).

Parameter names and values are URL-encoded, so spaces and special characters are safe.
With the sample schema and its defaults, the composed URL is:

```
https://example.com/landing?utm_source=qr&utm_medium=print&utm_campaign=spring_sale
```
