"""Rebuild the WPF vector, Windows ICO and PNG from Assets/App.svg.

Developer tools only: Inkscape 1.x on PATH and Python with Pillow.
"""
from io import BytesIO
from pathlib import Path
import struct
import subprocess
import tempfile
from xml.etree import ElementTree as ET

from PIL import Image

ASSETS = Path(__file__).resolve().parents[1] / "src/YubiEnroll.UI/Assets"
SVG = ASSETS / "App.svg"
SIZES = (16, 20, 24, 28, 32, 36, 40, 48, 64, 72, 80, 96, 128, 160, 192, 256)


def vector_source():
    svg = ET.parse(SVG).getroot()
    _, _, w, h = svg.attrib["viewBox"].split()
    group = svg.find("{*}g")
    return w, h, group


def write_wpf():
    # Keep path geometry, colors and transparent view box identical to the SVG.
    w, h, group = vector_source()
    paths = "\n".join(f'          <GeometryDrawing Brush="{p.attrib["fill"]}" Geometry="F0 {p.attrib["d"]}"/>'
                      for p in group.findall("{*}path"))
    (ASSETS / "BrandMark.xaml").write_text(f'''<!-- Generated from App.svg by tools/build_icon.py. -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <DrawingImage x:Key="BrandMark">
    <DrawingImage.Drawing>
      <DrawingGroup>
        <GeometryDrawing Brush="Transparent" Geometry="M0 0 H{w} V{h} H0 Z"/>
        <DrawingGroup>
{paths}
        </DrawingGroup>
      </DrawingGroup>
    </DrawingImage.Drawing>
  </DrawingImage>
</ResourceDictionary>
''', encoding="utf-8")


def rasterize(directory):
    raster = directory / "master.png"
    subprocess.run(["inkscape", str(SVG), "--export-width=2048", f"--export-filename={raster}"], check=True)
    with Image.open(raster) as source:
        master = source.convert("RGBA")
    frames = []
    for size in SIZES:
        icon = master.resize((size, size), Image.Resampling.LANCZOS)
        out = BytesIO()
        icon.save(out, format="PNG", optimize=True)
        frames.append(out.getvalue())
    # ICO uses 0 to represent 256 pixels in its one-byte width and height fields.
    directory_end = 6 + 16 * len(frames)
    entries = []
    for size, data in zip(SIZES, frames):
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), directory_end))
        directory_end += len(data)
    (ASSETS / "App.ico").write_bytes(struct.pack("<HHH", 0, 1, len(frames)) + b"".join(entries + frames))
    (ASSETS / "App.png").write_bytes(frames[-1])


if __name__ == "__main__":
    with tempfile.TemporaryDirectory() as tmp:
        rasterize(Path(tmp))
    write_wpf()
