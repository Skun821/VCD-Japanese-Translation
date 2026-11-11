import hashlib, json, os, shutil, sys
sys.path.insert(0, '/home/claude/inst')
from delta import make_delta, apply_delta

U = '/mnt/user-data/uploads/'
ST = U + 'common--Viscera/'          # current (installed) Steam files = targets
SB = U + 'VCD_JPN_Sandbox--Viscera/'
OUT = '/home/claude/inst/payload/'
shutil.rmtree(OUT, ignore_errors=True); os.makedirs(OUT + 'patches'); os.makedirs(OUT + 'jpn')

def sha(b): return hashlib.sha256(b).hexdigest().upper()

# target relative path -> (original source file in cloud, source relative path in the game, target file in cloud)
C = 'UDKGame/Content/'
P = [
    (C + 'Viscera/Gameplay/GP_Notes_Arch.upk', U + 'Viscera/' + C + 'Viscera/Gameplay/GP_Notes_Arch.upk', None),
    (C + 'Viscera/Gameplay/GP_Archetypes.upk', SB + C + 'Viscera/Gameplay/GP_Archetypes.upk', None),
    (C + 'Viscera/Maps/VC_MedBay.udk', SB + C + 'Viscera/Maps/VC_MedBay.udk', None),
    (C + 'Viscera/Maps/VC_Mantis_01.udk', SB + C + 'Viscera/Maps/VC_Mantis_01.udk', None),
    (C + 'Viscera/Maps/VC_SplatterStation.udk', SB + C + 'Viscera/Maps/VC_SplatterStation.udk', None),
    (C + 'Viscera/Maps/VC_Uprinsing.udk', SB + C + 'Viscera/Maps/VC_Uprinsing.udk', None),
    (C + 'Santa/Maps/V_Santa01.udk', SB + C + 'Santa/Maps/V_Santa01.udk', None),
    (C + 'Shadow/Maps/sw_temple.udk', SB + C + 'Shadow/Maps/sw_temple.udk', None),
]
FONT_VC = U + 'VisceraCleanupDetail/Build/FontBuildWork-PunchoutNews-20261103/UI_VC_Fonts.upk'
FONT_SH = U + 'VisceraCleanupDetail/Build/SharedFontBuildWork-PunchoutNews-20261103/UI_Fonts_Final.upk'
FONT_EN = U + 'Viscera/Engine/Content/EngineFonts.upk'
derived = [  # (target rel, source rel, source file)
    (C + 'Viscera/UI/UI_VC_Fonts.upk', C + 'Viscera/UI/UI_VC_Fonts.upk', FONT_VC),
    (C + 'Viscera/UI/UI_VC_Fonts_JPN.upk', C + 'Viscera/UI/UI_VC_Fonts.upk', FONT_VC),
    (C + 'Shared/UI_Fonts_Final.upk', C + 'Shared/UI_Fonts_Final.upk', FONT_SH),
    (C + 'Shared/UI_Fonts_Final_JPN.upk', C + 'Shared/UI_Fonts_Final.upk', FONT_SH),
    ('Engine/Content/EngineFonts.upk', 'Engine/Content/EngineFonts.upk', FONT_EN),
    ('Engine/Content/EngineFonts_JPN.upk', 'Engine/Content/EngineFonts.upk', FONT_EN),
    (C + 'Viscera/UI/UI_VC_Main_JPN.upk', C + 'Viscera/UI/UI_VC_Main.upk', ST + C + 'Viscera/UI/UI_VC_Main.upk'),
    (C + 'Viscera/UI/UI_VC_Vendor_JPN.upk', C + 'Viscera/UI/UI_VC_Vendor.upk', ST + C + 'Viscera/UI/UI_VC_Vendor.upk'),
]
entries = [(t, t, s) for t, s, _ in P] + derived

man = {'product': 'Viscera Cleanup Detail Japanese Localization', 'version': sys.argv[1] if len(sys.argv) > 1 else '1.0.0',
       'udk_version': '1.0.8767.0', 'originals': {}, 'patches': [], 'files': []}
cache = {}
for tgt_rel, src_rel, src_file in entries:
    src = open(src_file, 'rb').read(); tgt = open(ST + tgt_rel, 'rb').read()
    key = (sha(src), sha(tgt))
    if key not in cache:
        name = f'{len(cache):02d}_{os.path.basename(tgt_rel)}.vcdp'
        d = make_delta(src, tgt); assert apply_delta(src, d) == tgt
        open(OUT + 'patches/' + name, 'wb').write(d); cache[key] = name
        print(name, len(tgt), '->', len(d))
    o = man['originals'].setdefault(src_rel, {'sha256': sha(src), 'size': len(src)})
    assert o['sha256'] == sha(src)
    man['patches'].append({'target': tgt_rel.replace('/', '\\'), 'source': src_rel.replace('/', '\\'),
                           'source_sha256': sha(src), 'target_sha256': sha(tgt), 'target_size': len(tgt),
                           'patch': cache[key], 'new_file': tgt_rel != src_rel})
man['originals'] = {k.replace('/', '\\'): v for k, v in man['originals'].items()}
JP = '/home/claude/proj/Source/Localization/JPN/'
for f in sorted(os.listdir(JP)):
    if not f.endswith('.jpn') or f.endswith('.seed.jpn'): continue
    b = open(JP + f, 'rb').read(); shutil.copy(JP + f, OUT + 'jpn/' + f)
    man['files'].append({'target': 'UDKGame\\Localization\\JPN\\' + f, 'name': f, 'sha256': sha(b), 'size': len(b)})
json.dump(man, open(OUT + 'manifest.json', 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
tot = sum(os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(OUT) for f in fs)
print('payload bytes', tot, 'patches', len(cache), 'jpn', len(man['files']))
