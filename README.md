# QR Code Generator

<img src="Assets/app.png" align="right" width="96" alt="QR Code Generator icon" />

A small Windows desktop app (WPF) that turns URLs into QR codes — with **PNG and SVG**
output, vector **center glyphs pulled from any font**, a **color picker with contrast
checking**, a schema-driven URL builder, batch export, and a dark UI. The preview
regenerates as you work.

![QR Code Generator](docs/screenshot.png)

> **Note:** this is a side project maintained in spare time. Bug reports and ideas are
> welcome via [Issues](https://github.com/sascha-codeforfun/QrCodeGenerator/issues), but
> responses may be slow and there's no support guarantee.

## Download

Grab the latest **`QrCodeGenerator.exe`** from the
[Releases page](https://github.com/sascha-codeforfun/QrCodeGenerator/releases) and run it —
**no install needed.**

The build is self-contained (the .NET runtime and WPF are bundled into the exe), so it runs
on a clean machine with no .NET installed. Every release is provenance-attested and ships
with a SHA-256 checksum (`QrCodeGenerator.exe.sha256`).

> On first launch Windows SmartScreen may warn about an unsigned app — choose
> **More info → Run anyway**. That's expected for an unsigned binary.

## Requirements

- 64-bit Windows
- No .NET installation required (the runtime is bundled into the exe)

## Features

**Output**

- **PNG and SVG export** — the SVG stays razor-sharp at any size; PNG resolution is set by
  a pixels-per-module value.
- **Live preview** that regenerates automatically as you change fields — leave a field
  (or pick from a dialog) and the code updates. Invalid input just keeps the last good
  preview instead of interrupting you.
- **Preview background toggle** (white ⇄ dark) so you can eyeball how the code reads on a
  light or dark surface.

**Center graphic**

- Drop a graphic in the middle of the code: a raster image (PNG/JPG) or, better, a
  **vector glyph pulled straight from a font** (TTF/OTF/TTC) so it scales cleanly.
- **Choose font & glyph** in one dialog — pick a font, then **browse its glyphs in a grid**
  (paged, with code-point search) and click to build the character string. No need to know
  which key maps to which symbol.
- **System fonts…** lists your installed fonts by name and resolves the file for you — handy
  because Windows' `C:\Windows\Fonts` folder can't be browsed with a normal file dialog.
  **Browse…** still opens any font file anywhere on disk.
- Independent **glyph color** and **glyph background** colors. The background can be set to
  any color or turned off entirely (**none** = transparent), so the glyph can sit directly
  on the code instead of on a pad.

**Color picker**

- A reusable picker on every color field: a curated **palette**, plus **hex** and **R/G/B**
  inputs kept in sync, with a live preview.
- **Contrast hint** — for the dark/light pair and the glyph background it shows a WCAG
  contrast ratio and a plain-language rating, so you get an early warning before you make an
  unscannable code.

**URL building & batch**

- **Schema-driven URL builder** — define your parameters in `url-schema.json` and fill them
  in a tidy two-column form. A single default renders a textbox; a list of defaults renders
  a dropdown. Builds `DOMAIN/PREFIX?name=value&…` into the URL box.
- **Batch → ZIP** — paste a list of URLs and get a ZIP of QR `.svg` files. **All Single-tab
  settings** (colors, error correction, and the center graphic) are applied to every code,
  and a read-only summary on the Batch tab shows exactly what will be used. Filenames are
  derived from each URL with the protocol and subdomain stripped and every non `a–z`/`0–9`
  character replaced by an underscore.
- **Add to batch** — send a URL crafted on the Single tab straight to the batch list, with
  de-duplication so a double-click can't queue it twice.
- Adjustable error-correction level (L/M/Q/H) and fully custom colors throughout.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) on Windows.

```bash
dotnet run                 # build and launch
dotnet build -c Release    # release build
```

To produce the self-contained single-file exe locally:

```bash
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true
```

Releases are built automatically by the GitHub Actions workflow in
`.github/workflows/main.yml` when a release is published.

## The URL schema (`url-schema.json`)

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
| `domain` | yes | Base domain, including scheme. |
| `prefix` | no | Path after the domain; slashes are normalized. |
| `parameters[].name` | yes | The query key. |
| `parameters[].label` | no | Friendly label for the input; falls back to `name`. |
| `parameters[].default` | no | A single value → textbox; a list of values → dropdown (first pre-selected). |
| `parameters[].omitIfEmpty` | no | Defaults to `true`; a blank value is left out of the URL. |
| `parameters[].wide` | no | Force this field to span the full width of the two-column form. |

Parameters lay out two per row by default. A field spans the full width automatically when
its value (or longest dropdown option) is long, or when you set `"wide": true`.

The example schema ships next to the exe and auto-loads on startup; use **Load schema…**
to point at a different file.

## Fonts for the center glyph

WPF's glyph APIs read **TTF, OTF and TTC**. They do **not** read **WOFF/WOFF2** — convert
those to TTF/OTF first.

To pick a font, use **System fonts…** (installed fonts, by name) or **Browse…** (any font
file). The Windows `C:\Windows\Fonts` folder is a shell view that a normal file dialog
can't list, so **System fonts…** is the reliable way to reach installed fonts.

When you add a center graphic, prefer **High (H)** error correction and keep the graphic
modest in size — then scan it with your phone before relying on it.

## Trademarks

"QR Code" is a registered trademark of DENSO WAVE INCORPORATED. This project is not
affiliated with, sponsored by, or endorsed by Denso Wave. The trademark applies only to the
term "QR Code"; the underlying technology is open and free to use.

## License

MIT — see [LICENSE.txt](LICENSE.txt).
QR generation uses [QRCoder](https://github.com/codebude/QRCoder) (MIT).

## Built with Claude

Almost this entire app — features, workflow, this README — was created from a handful of
short, plain-English prompts to Claude. The full list of prompts for version 1.0.0 is in
[input.md](input.md), kept as-is to show how little input it took.

Everything after 1.0.0 — the dark theme, the font/glyph and color pickers, the glyph
background, the auto-updating preview, and a lot of layout tightening — came from a more
iterative loop: run it, screenshot what felt off, and describe the fix in a sentence or two.
The polish in 1.0.2 is as much "vibe-driven" UX tweaking as it is planned features, which is
exactly how this kind of tool tends to come together.
