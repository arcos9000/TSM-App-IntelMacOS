#!/usr/bin/env python3
"""Build TradeSkillMaster.app and its update ZIP without macOS tools.

Produces the same bundle the macOS job in .github/workflows/release.yml makes
with sips/iconutil/plutil/ditto, so it can run on Linux (or any OS with
Python 3 and Pillow). The ZIP holds TradeSkillMaster.app at its top level and
stores Unix permissions, which is the layout the in-app updater unpacks with
`ditto -x -k`.

Usage:
  build-app-bundle.py <publish-dir> <output-dir> <version> <arch>

  arch is the package suffix: x86_64 (Intel) or arm64 (Apple Silicon).
  Writes <output-dir>/TradeSkillMaster.app and
  <output-dir>/TSM-<version>-macos-<arch>.zip.
"""

import plistlib
import shutil
import stat
import sys
import zipfile
from pathlib import Path

from PIL import Image

APP_NAME = "TradeSkillMaster.app"
EXECUTABLE = "TSM"
ICON_NAME = "tsm-app"
# Pixel sizes matching the 16..512 (@1x and @2x) iconset the macOS job builds.
ICON_SIZES = [16, 32, 64, 128, 256, 512, 1024]
ZIP_UNIX_HOST = 3  # "create_system" value that tells unzip tools to honor Unix modes


def build_bundle(publish_dir: Path, app: Path, icon_png: Path, version: str) -> None:
    if app.exists():
        shutil.rmtree(app)
    macos_dir = app / "Contents" / "MacOS"
    resources_dir = app / "Contents" / "Resources"
    resources_dir.mkdir(parents=True)
    shutil.copytree(publish_dir, macos_dir, symlinks=True)
    executable = macos_dir / EXECUTABLE
    if not executable.is_file():
        raise SystemExit(f"Published executable not found: {executable}")
    executable.chmod(executable.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

    with Image.open(icon_png) as image:
        image.convert("RGBA").save(
            resources_dir / f"{ICON_NAME}.icns",
            sizes=[(size, size) for size in ICON_SIZES],
        )

    info = {
        "CFBundleName": "TradeSkillMaster Cross-Platform",
        "CFBundleDisplayName": "TradeSkillMaster Cross-Platform",
        "CFBundleIdentifier": "com.wuild.tsm-app",
        "CFBundleExecutable": EXECUTABLE,
        "CFBundleIconFile": ICON_NAME,
        "CFBundlePackageType": "APPL",
        "CFBundleShortVersionString": version,
        "CFBundleVersion": version,
        "NSHighResolutionCapable": True,
    }
    with open(app / "Contents" / "Info.plist", "wb") as handle:
        plistlib.dump(info, handle, fmt=plistlib.FMT_XML)


def zip_bundle(app: Path, archive: Path) -> None:
    root = app.parent
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as zf:
        paths = [app, *sorted(app.rglob("*"))]
        for path in paths:
            name = path.relative_to(root).as_posix()
            mode = path.lstat().st_mode
            if path.is_symlink():
                info = zipfile.ZipInfo(name)
                info.create_system = ZIP_UNIX_HOST
                info.external_attr = (mode & 0xFFFF) << 16
                zf.writestr(info, str(path.readlink()), zipfile.ZIP_STORED)
            elif path.is_dir():
                info = zipfile.ZipInfo.from_file(path, name)  # adds the trailing "/" and directory flags
                info.create_system = ZIP_UNIX_HOST
                zf.writestr(info, b"")
            else:
                info = zipfile.ZipInfo.from_file(path, name)
                info.create_system = ZIP_UNIX_HOST
                info.compress_type = zipfile.ZIP_DEFLATED
                with open(path, "rb") as source, zf.open(info, "w") as target:
                    shutil.copyfileobj(source, target, 1024 * 1024)


def main() -> None:
    if len(sys.argv) != 5:
        print(__doc__[__doc__.index("Usage:"):].strip(), file=sys.stderr)
        raise SystemExit(2)
    publish_dir = Path(sys.argv[1]).resolve()
    output_dir = Path(sys.argv[2]).resolve()
    version, arch = sys.argv[3], sys.argv[4]
    if arch not in ("x86_64", "arm64"):
        raise SystemExit(f"Unsupported arch '{arch}'; use x86_64 or arm64.")
    project_dir = Path(__file__).resolve().parents[2]

    output_dir.mkdir(parents=True, exist_ok=True)
    app = output_dir / APP_NAME
    build_bundle(publish_dir, app, project_dir / "TSM" / "Assets" / "tsm-app.png", version)
    archive = output_dir / f"TSM-{version}-macos-{arch}.zip"
    archive.unlink(missing_ok=True)
    zip_bundle(app, archive)
    print(archive)


if __name__ == "__main__":
    main()
