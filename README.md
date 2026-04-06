# StS2 BetaLauncher

This is a project forked from StS2 Launcher: https://github.com/Ekyso/StS2-Launcher

## Features

- **Steam authentication**  
  Login via SteamKit2 with Steam Guard 2FA support.
- **STS2 public-beta branch game file download**  
  Depot download directly from Steam, with update checking.
- **Cloud saves**  
  Full Steam cloud sync via SteamKit2's CCloud API, with timestamp-aware conflict resolution and non-blocking background uploads.
- **Mobile adaptation**  
  Touch input, UI scaling, layout adjustments, and app lifecycle handling via Harmony runtime patches.
- **LAN multiplayer**  
  UDP broadcast discovery and manual IP join.
- **Shader warmup**  
  Vulkan pipeline cache persistence and canvas ubershader support to eliminate first-encounter stutters.
- **Credential security**  
  Steam refresh tokens encrypted at rest via Android Keystore (AES-256-GCM, hardware-backed TEE).
- **Full Modding Capability**  
  Turn on "Local Backup" to load mods from `/storage/emulated/0/StS2BetaLauncher/Mods`.

## Changes from the original version

1. Modified DepotDownloader.cs to download the latest public-beta version of STS2.
2. Modified ModLoaderPatches.cs to fully support the new modding format.
3. Changed the package name, etc. to coexist with StS2 Launcher.

## Project Structure

```
src/STS2Mobile/
  ModEntry.cs              # Entry point ([UnmanagedCallersOnly] Apply())
  PatchHelper.cs           # Shared patch utility + logging
  Patches/                 # Harmony patches (one file per concern)
  Launcher/                # Programmatic Godot UI (MVC)
  Steam/                   # SteamKit2 login, depot download, cloud saves
android/                   # Godot Android gradle project
  src/.../GodotApp.java    # Activity, assembly setup, Keystore encryption
  assets/bootstrap.pck     # Minimal PCK for launcher-only mode
src/stubs/                 # Native library stubs (Steam API, Sentry)
scripts/                   # Build and tooling scripts
```

## Prerequisites

The following prerequisites are copied from the `README.md` of the original repo.

If one prerequisite is optional, then it is techically unnecessary if you only want to generate an apk.

- .NET 9 SDK (9.0.312 Recommended)
- Android SDK35 + NDK r28b
- Python 3 (Optional, if you want to run `make-bootstrap-pck.py` to make bootstraper.pck or use SCons to compile godot yourself)
- Original game files in `upstream/godot-export/` (Optional)
- Godot Engine (Necessary, put in `vendor\godot\`; Custom build is optional, see `scripts\build-godot.sh`)
- FMOD SDK in `vendor\fmod-sdk\` (Optional)

## Building Tutorial on Windows

**Note: The tutorial is only intended to generate installable an apk, if you want to compile all components yourself, see the README.md in the original repo. (In my test, it seemed almost impossible under a Windows environment.)** 

Resource pack: 通过网盘分享的文件：StS2BetaLauncherResourcePack.zip
链接: https://pan.baidu.com/s/1StR0xKRUrMl0o2m8EhlaxA?pwd=y7w2 提取码: y7w2

Only for learning use!

1. Git clone the project, extract the resource pack to the root folder, modify the source code as you wish, remember to set the sdk directory in android\local.properties
2. `cd StS2-BetaLauncher\src\`, `csharpier format .`, `dotnet publish -c Release`, `cd ..\android\`
3. Find `STS2Mobile.dll` in `src\bin\Release\net9.0\publish\`
4. Copy it to `android\assets\dotnet_bcl`
5. Put precompiled assets in the same folder.
6. Put `fmod.jar` and `godot-lib.4.5.1.stable.template_release.aar` in `android\libs\release\`
7. Put .so libs in `android\libs\release\arm64-v8a\`
8. `gradle wrapper` (Optional, if you want to regenerate gradlew.bat)
9. `.\gradle clean`, `.\gradlew assembleMonoRelease`

Output: `android/build/outputs/apk/mono/release/StS2BetaLauncher-v<version>.apk`

PS: You can change to your own keystore or simply use mine.

### Installing

```bash
adb install -r android/build/outputs/apk/mono/release/StS2BetaLauncher-v*.apk

# Fresh install (clear saved credentials + cached assemblies)
adb shell pm clear com.game.sts2betalauncher
```

### Other build tasks

```bash
# Regenerate bootstrap PCK (only if project.godot changes)
python3 scripts/make-bootstrap-pck.py

# Rebuild Godot engine (only if engine source changes)
bash scripts/build-godot.sh

# Rebuild native stubs (requires Android NDK)
bash src/stubs/build_stubs.sh
```

```bash
## License

This project is licensed under the [MIT License](LICENSE). See [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) for third-party dependency licenses.

FMOD requires a commercial license if your project generates revenue. Spine Runtimes require a valid Spine Editor license. See the third-party licenses file for details.
