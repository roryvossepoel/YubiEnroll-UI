"""Render the canonical community app mark into PNG/ICO. Requires Pillow only.
Same flat #84BD00 tile and slanted white USB key are used on every surface.
"""
from pathlib import Path
from PIL import Image, ImageDraw
root = Path(__file__).resolve().parents[1] / 'src/YubiEnroll.UI/Assets'
size = 1024
im = Image.new('RGBA', (size, size))
d = ImageDraw.Draw(im)
d.rounded_rectangle((16,16,1008,1008), radius=150, fill='#84BD00')
key = Image.new('RGBA', (size,size)); k = ImageDraw.Draw(key)
k.rounded_rectangle((330,145,694,700), radius=75, fill='white')
k.ellipse((463,223,561,321), fill='#84BD00')
k.rectangle((401,700,623,866), fill='white')
k.rectangle((430,739,468,820), fill='#84BD00');k.rectangle((554,739,592,820), fill='#84BD00')
key=key.rotate(-40, resample=Image.Resampling.BICUBIC)
im.alpha_composite(key)
im=im.resize((256,256),Image.Resampling.LANCZOS)
im.save(root/'App.png');im.save(root/'App.ico',sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])
(root/'App.svg').write_text('''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024"><rect x="16" y="16" width="992" height="992" rx="150" fill="#84BD00"/><g transform="rotate(40 512 512)"><rect x="330" y="145" width="364" height="555" rx="75" fill="white"/><circle cx="512" cy="272" r="49" fill="#84BD00"/><path d="M401 700H623V866H401Z" fill="white"/><path d="M430 739H468V820H430ZM554 739H592V820H554Z" fill="#84BD00"/></g></svg>''')
