#!/bin/sh
# Builds VisceraJapaneseInstaller.exe on Linux/macOS with Mono (mono-devel).
cd "$(dirname "$0")"
A=/usr/lib/mono/4.5-api
R="-resource:payload/manifest.tsv,manifest.tsv"
for f in payload/patches/*; do R="$R -resource:$f,patches/$(basename $f)"; done
for f in payload/jpn/*; do R="$R -resource:$f,jpn/$(basename $f)"; done
mcs -target:winexe -platform:anycpu -optimize+ -codepage:utf8 -win32icon:app.ico -nostdlib \
  -r:$A/mscorlib.dll -r:$A/System.dll -r:$A/System.Core.dll -r:$A/System.Windows.Forms.dll -r:$A/System.Drawing.dll \
  -out:VisceraJapaneseInstaller.exe $R Program.cs
