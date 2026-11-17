# Compressed images and image archives. Sourced by verify-linux.sh, which provides $F (the CLI), tree.ref (the
# listing of the test tree), img/xz.sqfs (the same tree as SquashFS), ok/bad and cmpdump; everything else lives
# in arc/.
#
#   arc_run     an image packed by the reference tools is read by FsForge: gzip and pigz, xz (one block, several
#               blocks with -T, several streams in a row), zstd and pzstd, bzip2, pbzip2 and lbzip2, zip (deflate
#               and stored, with a README beside the image), 7z (LZMA2 with several threads, LZMA, BZip2, Deflate,
#               a BCJ filter, several files, an encrypted archive that must be refused cleanly). Images written by
#               FsForge under each name are checked and unpacked by the same tools. An image edited in place
#               in a zip or 7z archive keeps the other files, sha256sum -c accepts the updated checksum file and
#               bmaptool copies the image with the rebuilt block map (bmaptool checks every range).
#   arc_flashtool   with FLASHTOOL_SRC=<checkout of FlashTool>: a small program built on FlashTool.Core creates
#               gz, xz, zst, bz2 and zip images with FlashTool's own code (bz2 through SharpZipLib, which writes
#               randomised blocks), FsForge edits them in place, and FlashTool's code then finds the checksum,
#               accepts the block map and reads the edited image. 7z needs 7z.dll (Windows): its 7-Zip engine is
#               covered by arc_run.

arc_cmp() { # name file : FsForge reads it and sees the test tree
  if $F dump "$2" > "arc/out/$1.txt" 2> "arc/out/$1.err" && cmp -s "arc/out/$1.txt" tree.ref; then ok "read  $1"
  else bad "read  $1: $(grep -v '^warning' "arc/out/$1.err" | head -c 200)"; diff "arc/out/$1.txt" tree.ref | head -4 | cut -c1-200; fi
}

arc_run() {
  rm -rf arc; mkdir -p arc/img arc/out arc/x
  echo "Archives: images packed by the reference tools"
  local I=arc/img src=arc/src.img
  cp img/xz.sqfs $src
  printf 'flash me\n' > arc/README.txt
  have() { command -v "$1" >/dev/null; }
  have gzip   && { gzip -c $src > $I/a.img.gz; arc_cmp gzip $I/a.img.gz; }
  have pigz   && { pigz -c -p 4 $src > $I/b.img.gz; arc_cmp pigz $I/b.img.gz; }
  have xz     && { xz -c -T1 $src > $I/c.img.xz; arc_cmp "xz one block" $I/c.img.xz
                   xz -c -T4 --block-size=1MiB $src > $I/d.img.xz; arc_cmp "xz -T4, blocks of 1 MiB" $I/d.img.xz
                   head -c 3000000 $src | xz -c > $I/e.img.xz; tail -c +3000001 $src | xz -c >> $I/e.img.xz; arc_cmp "xz, two streams in a row" $I/e.img.xz; }
  have zstd   && { zstd -q -c $src > $I/f.img.zst; arc_cmp zstd $I/f.img.zst; }
  have pzstd  && { pzstd -q -p 4 -c $src > $I/g.img.zst 2>/dev/null; arc_cmp "pzstd (several frames)" $I/g.img.zst; }
  have bzip2  && { bzip2 -c $src > $I/h.img.bz2; arc_cmp bzip2 $I/h.img.bz2; }
  have pbzip2 && { pbzip2 -c -p4 $src > $I/i.img.bz2; arc_cmp "pbzip2 (several streams)" $I/i.img.bz2; }
  have lbzip2 && { lbzip2 -c -n4 $src > $I/j.img.bz2; arc_cmp lbzip2 $I/j.img.bz2; }
  if have zip; then
    (cd arc && cp src.img rootfs.img && zip -q img/k.zip README.txt rootfs.img && zip -q -0 img/l.zip rootfs.img README.txt && rm rootfs.img)
    arc_cmp "zip deflate, README beside the image" $I/k.zip; arc_cmp "zip stored" $I/l.zip
  fi
  if have 7z; then
    local v n o
    (cd arc && cp src.img rootfs.img)
    for v in "m lzma2 -m0=lzma2 -mmt=4" "n lzma -m0=lzma" "o bzip2 -m0=bzip2" "p deflate -m0=deflate" "q bcj -mf=BCJ -m0=lzma2" "r nonsolid -ms=off -m0=lzma2"; do
      set -- $v; n=$1; o=$2; shift 2
      (cd arc && 7z a -bd -y img/$n.7z README.txt rootfs.img "$@" >/dev/null 2>&1) && arc_cmp "7z $o" $I/$n.7z || bad "7z $o could not be made"
    done
    (cd arc && 7z a -bd -y -pSecret img/s.7z rootfs.img >/dev/null 2>&1)
    $F dump $I/s.7z > /dev/null 2> arc/out/s.err && bad "an encrypted 7z archive was opened" \
      || { grep -q "encrypted" arc/out/s.err && ok "an encrypted 7z archive is refused with a clear message" || bad "encrypted 7z: $(head -c 200 arc/out/s.err)"; }
    rm -f arc/rootfs.img
  fi

  echo "Archives: images written by FsForge"
  local x t
  for x in img.gz img.xz img.zst img.bz2 zip 7z; do
    t=arc/img/w.$x
    if ! $F convert img/xz.sqfs $t format=squashfs verify=content 2> arc/out/w.$x.err; then bad "write $x: $(grep -v '^warning' arc/out/w.$x.err | head -c 200)"; continue; fi
    rm -rf arc/x/*
    case $x in
      img.gz)  gzip -dc $t > arc/x/w.img ;;
      img.xz)  xz -dc $t > arc/x/w.img ;;
      img.zst) zstd -q -dc $t > arc/x/w.img ;;
      img.bz2) bzip2 -dc $t > arc/x/w.img ;;
      zip)     unzip -q $t -d arc/x && unzip -tq $t > /dev/null ;;
      7z)      7z x -bd -oarc/x $t > /dev/null && 7z t $t > /dev/null ;;
    esac
    local r=$?
    local f; f=$(ls arc/x | head -1)
    if [ $r = 0 ] && [ -n "$f" ] && $F dump "arc/x/$f" 2>/dev/null | cmp -s - tree.ref; then ok "write $x ($(stat -c %s $t) bytes): the reference tool unpacks '$f'"
    else bad "write $x: the reference tool rejects it or unpacks something else ($r, '$f')"; fi
  done

  echo "Archives: editing in place"
  for x in zip 7z; do
    t=arc/img/fw.$x
    case $x in zip) cp arc/img/k.zip $t ;; 7z) cp arc/img/m.7z $t ;; esac
    (cd arc/img && sha256sum fw.$x > fw.$x.sha256)
    if $F edit $t $t rm:/one-byte mkdir:/added 2> arc/out/e.$x.err; then
      rm -rf arc/x/*
      case $x in zip) unzip -q $t -d arc/x ;; 7z) 7z x -bd -oarc/x $t > /dev/null ;; esac
      local listing; listing=$($F ls arc/x/rootfs.img / 2>/dev/null)
      (cd arc/img && sha256sum -c --quiet fw.$x.sha256 >/dev/null 2>&1) && cmp -s arc/x/README.txt arc/README.txt \
        && echo "$listing" | grep -q " added$" && ! echo "$listing" | grep -q " one-byte$" \
        && ok "$x edited in place: README.txt kept, sha256sum -c accepts the updated checksum" || bad "$x edited in place: $(head -c 200 arc/out/e.$x.err)"
    else bad "edit $x: $(head -c 200 arc/out/e.$x.err)"; fi
  done
  if command -v bmaptool >/dev/null && command -v sfdisk >/dev/null; then
    # A disk image with a block map, as FlashTool saves it: the map must be rebuilt for the edited image.
    truncate -s 64M arc/disk.img
    printf 'start=8192, size=65536, type=83\n' | sfdisk -q arc/disk.img >/dev/null 2>&1
    dd if=$src of=arc/disk.img bs=1M seek=4 conv=notrunc status=none
    bmaptool create -o arc/img/disk.img.bmap arc/disk.img 2>/dev/null
    xz -c -T4 arc/disk.img > arc/img/disk.img.xz; rm arc/disk.img
    if $F edit arc/img/disk.img.xz arc/img/disk.img.xz rm:/one-byte 2> arc/out/bm.err && grep -q "updated: disk.img.bmap" arc/out/bm.err \
       && bmaptool copy arc/img/disk.img.xz arc/x/disk.img > arc/out/bmcopy.txt 2>&1 \
       && $F ls arc/x/disk.img / > arc/out/bmls.txt 2>/dev/null && ! grep -q " one-byte$" arc/out/bmls.txt; then
      ok "the block map is rebuilt: bmaptool checks it and copies the edited disk image"
    else bad "block map: $(tail -2 arc/out/bm.err) $(tail -2 arc/out/bmcopy.txt)"; fi
  else echo "  --    bmaptool or sfdisk is not installed: block map skipped"; fi
}

arc_flashtool() {
  local src=${FLASHTOOL_SRC:-}
  if [ -z "$src" ] || [ ! -f "$src/src/FlashTool.Core/FlashTool.Core.csproj" ]; then echo "  --    FlashTool checks skipped (set FLASHTOOL_SRC to a checkout of FlashTool)"; return; fi
  command -v dotnet >/dev/null || { echo "  --    FlashTool checks skipped: dotnet is not installed"; return; }
  command -v bmaptool >/dev/null && command -v sfdisk >/dev/null || { echo "  --    FlashTool checks skipped: bmaptool and sfdisk are needed"; return; }
  echo "Archives: FlashTool's own code"
  mkdir -p arc/ft
  cat > arc/ft/ft.csproj <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
  <AssemblyName>FlashTool.Core.Tests</AssemblyName><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup>
  <ItemGroup><ProjectReference Include="$src/src/FlashTool.Core/FlashTool.Core.csproj" /></ItemGroup>
</Project>
EOF
  cat > arc/ft/Program.cs <<'EOF'
// "create <format> <raw> <out>", "read <archive> <raw out>", "check <file>": FlashTool.Core does the work.
// The assembly name is the one FlashTool.Core grants its internals to (for BmapImageCheck).
using FlashTool.Core.Bmap;
using FlashTool.Core.Checksums;
using FlashTool.Core.Containers;
using FlashTool.Core.Sources;
foreach (var lib in new[] { "/usr/lib/x86_64-linux-gnu/liblzma.so.5", "/usr/lib/aarch64-linux-gnu/liblzma.so.5" })
    if (File.Exists(lib)) { XzContainerHandler.Initialize(lib); break; }
var reg = ContainerRegistry.Default;
switch (args[0])
{
    case "create":
    {
        var h = reg.Get(Enum.Parse<ContainerFormat>(args[1], true));
        using (var o = h.Create(File.Create(args[3]), ContainerNaming.GetInnerName(args[3]), new FileInfo(args[2]).Length, new CompressionSettings(CompressionPreset.Normal, 4)))
        using (var i = File.OpenRead(args[2])) i.CopyTo(o);
        break;
    }
    case "read":
    {
        using var c = reg.Open(new FileStreamSource(args[1]));
        using var r = c.OpenEntry(c.Entries[0]);
        using var o = File.Create(args[2]);
        r.Data.CopyTo(o);
        break;
    }
    case "check":
    {
        var found = ChecksumDiscovery.FindForFile(args[1]) ?? throw new Exception("no checksum file found");
        using (var f = File.OpenRead(args[1]))
            if (!System.Security.Cryptography.SHA256.HashData(f).AsSpan().SequenceEqual(found.Sha256)) throw new Exception("the checksum differs");
        var bm = BmapDiscovery.FindForFile(args[1]);
        if (bm.Bmap is null) throw new Exception($"no usable block map ({bm.Problem})");
        using var c = reg.Open(new FileStreamSource(args[1]));
        using var r = c.OpenEntry(c.Entries[0]);
        using var check = new BmapImageCheck(bm.Bmap);
        var buf = new byte[1 << 20];
        int n;
        while ((n = r.Data.Read(buf)) > 0) check.Process(buf.AsSpan(0, n));
        Console.WriteLine("checksum and block map accepted");
        break;
    }
}
EOF
  if ! dotnet build arc/ft -c Release -o arc/ft/bin > arc/out/ft.build 2>&1; then bad "FlashTool.Core could not be built: $(grep -m1 error arc/out/ft.build)"; return; fi
  local FT="dotnet arc/ft/bin/FlashTool.Core.Tests.dll"
  truncate -s 64M arc/ftdisk.img
  printf 'start=8192, size=65536, type=83\n' | sfdisk -q arc/ftdisk.img >/dev/null 2>&1
  dd if=arc/src.img of=arc/ftdisk.img bs=1M seek=4 conv=notrunc status=none
  local fmt ext n
  for fmt in Gzip Xz Zstd Bzip2 Zip; do
    ext=$(case $fmt in Gzip) echo img.gz;; Xz) echo img.xz;; Zstd) echo img.zst;; Bzip2) echo img.bz2;; Zip) echo zip;; esac)
    n=arc/img/ft.$ext
    if ! $FT create $fmt arc/ftdisk.img $n 2> arc/out/ft.$fmt.err; then bad "FlashTool cannot create $fmt: $(head -c 200 arc/out/ft.$fmt.err)"; continue; fi
    (cd arc/img && sha256sum ft.$ext > ft.$ext.sha256)
    # FlashTool looks for "ft.img.bmap" next to "ft.img.gz" and for "ft.bmap" next to "ft.zip".
    bmaptool create -o "arc/img/$(basename $n .${ext##*.}).bmap" arc/ftdisk.img 2>/dev/null
    if $F edit $n $n rm:/one-byte 2> arc/out/fte.$fmt.err && $FT check $n > arc/out/ftc.$fmt.txt 2>&1 && $FT read $n arc/x/ft.img 2>/dev/null \
       && ! $F ls arc/x/ft.img / 2>/dev/null | grep -q " one-byte$"; then
      ok "FlashTool $fmt: FsForge edits it; FlashTool accepts the checksum and the block map and reads the edited image"
    else bad "FlashTool $fmt: $(grep -v '^warning\|^updated' arc/out/fte.$fmt.err | head -c 150) $(tail -c 200 arc/out/ftc.$fmt.txt)"; fi
  done
}
