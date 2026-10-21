# OpenWeather image dependency constraints

Status: migration deferred. This note records the dependency decision as of
2026-10-21 so that routine dependency updates do not accidentally introduce a
build-time license-key requirement or an incomplete cross-platform package.

## Current ImageSharp ceiling

The module is intentionally pinned to:

- `SixLabors.ImageSharp` `3.1.12`
- `SixLabors.ImageSharp.Drawing` `2.1.7`

These are the latest releases in the last version lines that do not enforce a
Six Labors license key at build time. Do not update either dependency in
isolation:

- ImageSharp starts build-time license-key enforcement at `3.2.0` in the 3.x
  line and at `4.0.0` in the current major line.
- ImageSharp.Drawing starts enforcement at `2.2.0` in the 2.x line and at
  `3.0.0` in the current major line.

This does **not** mean that the pinned versions are license-free. They remain
subject to the Six Labors Split License. It only means that builds do not need
an issued key. Six Labors documents how an issued key can be supplied through
`sixlabors.lic`, an MSBuild property, or an environment variable, but no public
API for fully automatic initial issuance or renewal was found during this
investigation. Moving onto an enforced version would therefore introduce an
ongoing administrative dependency unless that process changes.

The current ImageSharp `3.1.12` package has published security advisories. The
pin is a known temporary tradeoff, not a reason to suppress vulnerability
reporting. Before changing it, either accept and document the Six Labors key
workflow or replace ImageSharp.

References:

- [ImageSharp licensing and key enforcement](https://docs.sixlabors.com/articles/imagesharp/index.html?tabs=tabid-1)
- [ImageSharp.Drawing licensing and key enforcement](https://docs.sixlabors.com/articles/imagesharp.drawing/index.html)
- [ImageSharp 3.1.12 advisories on NuGet](https://www.nuget.org/packages/SixLabors.ImageSharp/3.1.12)
- [ImageSharp.Drawing 2.1.7 on NuGet](https://www.nuget.org/packages/SixLabors.ImageSharp.Drawing/2.1.7)

## Image functionality that must be preserved

ImageSharp is used only by weather-map generation. A replacement must:

1. decode OpenStreetMap and OpenWeather PNG tiles;
2. alpha-composite the weather layer over the base-map layer;
3. stitch multiple 256-by-256 tiles into one image;
4. render the required `© OpenStreetMap contributors` attribution; and
5. encode the result as PNG.

Forecasts, current weather, air-quality data, settings, and chat augmentation
behavior do not depend on the image library. Replacing it does not require
removing those features. Relevant implementation files are
`Clients/OpenWeatherClient.cs`, `Helper/TileFetcher.cs`, and
`Helper/ImageExtensions.cs`.

## Preferred replacement for minimum maintenance

The lowest-maintenance candidate is `StbImageSharp` plus
`StbImageWriteSharp`:

- both are pure managed C# and need no platform-native binaries;
- one package can run on Windows, macOS, and Linux;
- they use permissive MIT/public-domain licensing and require no license key;
- a minimal .NET 10 publish containing both libraries measured 131,218 bytes
  in total, of which the two STB assemblies were 114,688 bytes; and
- `dotnet list package --vulnerable --include-transitive` reported no known
  advisories from the current NuGet sources on 2026-10-21.

This is not an API-level drop-in replacement. The module would need a small,
contained pixel-buffer implementation for alpha blending and tile copying.
Attribution should be an embedded bitmap asset or use a small embedded bitmap
font so that output does not depend on fonts installed on the host. PNG inputs
should be size-limited and validated before decoding because they are network
content.

`StbImageWriteSharp` `1.16.7` was last published to NuGet in 2022 even though
its source repository has received later activity. That release cadence is the
main maintenance caveat.

References:

- [StbImageSharp project](https://github.com/StbSharp/StbImageSharp)
- [StbImageSharp 2.30.16](https://www.nuget.org/packages/StbImageSharp/2.30.16)
- [StbImageWriteSharp 1.16.7](https://www.nuget.org/packages/StbImageWriteSharp/1.16.7)

## SkiaSharp alternative

SkiaSharp is the higher-level alternative and is MIT licensed, but it relies
on platform-native binaries. `SkiaSharp.NativeAssets.Linux.NoDependencies`
does help Linux deployment: it supplies glibc and musl variants without
Fontconfig or other third-party libraries beyond the standard C runtime
dependencies. Because Fontconfig is excluded, system-font discovery is not
available; the attribution font or bitmap must be embedded explicitly.

A single framework-dependent package containing all Windows, macOS, and Linux
assets measured about 82.4 MB compressed after excluding native PDB files.
That exceeds the 32 MB ZIP limit documented by the Voxta module release action.
SkiaSharp would therefore require runtime-specific module packages rather than
the current single platform-neutral package.

Measurements from a minimal .NET 10 project using SkiaSharp `4.153.1` and
`SkiaSharp.NativeAssets.Linux.NoDependencies` `4.153.1`:

| Runtime | Compressed publish size |
| --- | ---: |
| `win-x64` | 29.8 MB with the 96 MB native PDB; 5.7 MB without it |
| `linux-x64` | 5.1 MB |
| `linux-arm64` | 5.1 MB |
| `osx-x64` | 7.8 MB |
| `osx-arm64` | 7.8 MB |

These are probe measurements, not final module sizes. A SkiaSharp migration
would additionally require:

- a release matrix for each supported runtime identifier;
- removal of the Windows native PDB from distributable packages;
- aggregation of the per-runtime packages into the Voxta release manifest;
- verification that native-library resolution works for a dynamically loaded
  Voxta module on each operating system; and
- real tests on Windows, macOS, glibc Linux, and musl Linux where supported.

The release matrix can be automated and has no license-renewal burden, but it
adds release infrastructure that the pure-managed STB approach avoids.

References:

- [SkiaSharp package and deployment guide](https://github.com/mono/SkiaSharp/blob/main/documentation/dev/packages.md)
- [SkiaSharp.NativeAssets.Linux.NoDependencies 4.153.1](https://www.nuget.org/packages/SkiaSharp.NativeAssets.Linux.NoDependencies/4.153.1)
- [Voxta module release action](https://github.com/voxta-ai/voxta-module-actions/tree/v1)

## Other alternatives considered

- **Magick.NET:** cross-platform and capable, but its native distribution is
  large and excessive for PNG tile composition.
- **NetVips:** capable and cross-platform, but introduces `libvips`, LGPL
  considerations, and native deployment complexity.
- **System.Drawing.Common:** Windows-only for supported modern .NET scenarios;
  unsuitable for this module.
- **Microsoft.Maui.Graphics:** requires a rendering backend and adds framework
  coupling without simplifying headless module deployment.

## Decision for a future migration

If minimum ongoing project maintenance remains the priority, implement the STB
replacement and retain one platform-neutral module package. Choose SkiaSharp
only if its higher-level drawing API justifies owning the runtime-specific
release matrix and cross-platform native-loading tests.
