# cpio checks. Sourced by verify-linux.sh, which provides $F (the CLI), scan.py, tree/ with its listing
# tree.ref, img/xz.sqfs (the same tree), ok/bad and cmpdump; everything else lives in cpio/.
#
#   cpio_run    archives made by GNU cpio (newc, crc and odc; with and without "./" in front of the names and
#               an entry for the root; gzip, xz and zstd) and by bsdtar are read by FsForge; an initramfs of
#               two archives (uncompressed microcode, then the compressed system) is read as one tree. Archives
#               written by FsForge (newc and crc, every compression, "./" names, no root entry) are unpacked by
#               GNU cpio, bsdtar and busybox and compared entry by entry with the source tree.
#   cpio_qemu   with CPIO_KERNEL=<vmlinuz> and CPIO_BUSYBOX=<static busybox> (and qemu-system-x86_64): the
#               kernel boots every archive FsForge writes as its initramfs, and a script started as /init
#               lists what the kernel unpacked (types, modes, owners, times, sizes, links, devices, hashes).
#
# cpio keeps no extended attributes: the listings are compared without them.

cpio_dec() { case $1 in *.gz) gzip -dc "$1";; *.xz) xz -dc "$1";; *.zst) zstd -dc "$1";; *) cat "$1";; esac; }

cpio_cmp() { # name dir ref [from-line] [skip-regex] : an unpacked folder against a reference (no attributes)
  # GNU cpio and bsdtar give the folder they unpack into no time, and GNU cpio makes the hard links of a file
  # after it has set the time of their folder (also in archives GNU cpio made): times of folders are not compared.
  local dt='BEGIN{FS=OFS="\t"} $2=="d"{$6="-"} 1'
  python3 scan.py "$2" | cut -f1-8 | tail -n +${4:-1} | awk "$dt" | grep -v "${5:-^$}" > cpio/out/x.$1.txt
  cut -f1-8 "$3" | tail -n +${4:-1} | awk "$dt" | grep -v "${5:-^$}" | cmp -s - cpio/out/x.$1.txt && return 0
  diff cpio/out/x.$1.txt <(cut -f1-8 "$3" | tail -n +${4:-1} | awk "$dt" | grep -v "${5:-^$}") | head -4 | cut -c1-200; return 1
}

cpio_rd() { # name ref [from-line] [awk] : FsForge reads cpio/img/<name>
  local n=$1 ref=$2 from=${3:-1} f=${4:-1}
  if $F dump cpio/img/$n 2> cpio/out/r.$n.err | cut -f1-8 | tail -n +$from | awk "$f" > cpio/out/r.$n.txt && cut -f1-8 $ref | tail -n +$from | awk "$f" | cmp -s - cpio/out/r.$n.txt
  then ok "read  cpio $n"; else bad "read  cpio $n: $(head -c 200 cpio/out/r.$n.err)"; diff cpio/out/r.$n.txt <(cut -f1-8 $ref | tail -n +$from | awk "$f") | head -4 | cut -c1-200; fi
}

cpio_run() {
  rm -rf cpio; mkdir -p cpio/img cpio/out
  echo "cpio: archives made by GNU cpio${BSD:+ and bsdtar}"
  if ! command -v cpio >/dev/null; then echo "  --    GNU cpio is not installed: skipped"; return; fi
  (cd tree && find . | cpio -o -H newc --quiet) > cpio/img/newc.cpio;               cpio_rd newc.cpio tree.ref
  (cd tree && find . | cpio -o -H crc --quiet) > cpio/img/crc.cpio;                 cpio_rd crc.cpio tree.ref
  (cd tree && find . -depth | cpio -o -H newc --quiet) > cpio/img/depth.cpio;       cpio_rd depth.cpio tree.ref   # a directory after what it holds
  (cd tree && find . -mindepth 1 -printf '%P\n' | cpio -o -H newc --quiet) > cpio/img/noprefix.cpio; cpio_rd noprefix.cpio tree.ref 2  # no "./", no root entry
  gzip -9c cpio/img/newc.cpio > cpio/img/newc.cpio.gz;                              cpio_rd newc.cpio.gz tree.ref
  xz -c --check=crc32 cpio/img/newc.cpio > cpio/img/newc.cpio.xz;                   cpio_rd newc.cpio.xz tree.ref
  command -v zstd >/dev/null && { zstd -q -c cpio/img/newc.cpio > cpio/img/newc.cpio.zst; cpio_rd newc.cpio.zst tree.ref; }
  # odc cannot hold every owner and device number of the tree: what GNU cpio itself unpacks is the reference.
  (cd tree && find . | cpio -o -H odc --quiet 2>/dev/null) > cpio/img/odc.cpio
  rm -rf cpio/x; mkdir cpio/x; (cd cpio/x && cpio -idmu --quiet --no-absolute-filenames < ../img/odc.cpio 2>/dev/null)
  python3 scan.py cpio/x > cpio/out/odc.ref; rm -rf cpio/x; cpio_rd odc.cpio cpio/out/odc.ref 2 'BEGIN{FS=OFS="\t"} $2=="d"{$6="-"} 1'   # see cpio_cmp
  if command -v bsdtar >/dev/null; then
    (cd tree && bsdtar -c --format=newc -f ../cpio/img/bsd_newc.cpio . 2>/dev/null); cpio_rd bsd_newc.cpio tree.ref
  fi

  # An initramfs as distributions build it: the CPU microcode uncompressed, then the system compressed.
  rm -rf cpio/early; mkdir -p cpio/early/kernel/x86/microcode; echo microcode > cpio/early/kernel/x86/microcode/GenuineIntel.bin
  (cd cpio/early && find . | cpio -o -H newc --quiet) > cpio/img/initrd.img; cat cpio/img/newc.cpio.gz >> cpio/img/initrd.img
  $F dump cpio/img/initrd.img 2> cpio/out/initrd.err | cut -f1-8 | grep -v "^/kernel" > cpio/out/initrd.txt
  $F dump cpio/img/initrd.img 2>/dev/null | grep -q "^/kernel/x86/microcode/GenuineIntel.bin	f" && cut -f1-8 tree.ref | cmp -s - cpio/out/initrd.txt \
    && grep -q "2 cpio archives one after another" cpio/out/initrd.err && ok "read  cpio initramfs of two archives (microcode + gzip)" \
    || bad "initramfs of two archives: $(head -c 300 cpio/out/initrd.err)"
  head -c $(( $(stat -c %s cpio/img/newc.cpio) - 700 )) cpio/img/newc.cpio > cpio/img/cut.cpio
  $F dump cpio/img/cut.cpio > /dev/null 2> cpio/out/cut.err; grep -qi "truncated" cpio/out/cut.err && ok "a truncated cpio archive is reported" || bad "truncated cpio: $(cat cpio/out/cut.err)"

  echo "cpio: archives written by FsForge"
  local src=img/xz.sqfs
  [ -f $src ] || { mksquashfs tree cpio/img/src.sqfs -noappend -quiet -no-progress >/dev/null 2>&1; src=cpio/img/src.sqfs; }
  cw() { # name options... : write, then unpack with GNU cpio, bsdtar and busybox
    local n=$1; shift; local img=cpio/img/w_$n from=1 r
    case " $* " in *" root=0 "*) from=2;; esac
    if ! $F convert $src $img format=cpio verify=content "$@" 2> cpio/out/w.$n.err; then bad "write cpio $n: $(cat cpio/out/w.$n.err)"; return; fi
    rm -rf cpio/x; mkdir cpio/x
    if (cpio_dec $img | (cd cpio/x && cpio -idmu --quiet --no-absolute-filenames 2> ../out/x.$n.err)) && cpio_cmp $n cpio/x tree.ref $from
    then ok "write cpio $n ($(stat -c %s $img) bytes; GNU cpio)"; else bad "write cpio $n: GNU cpio extraction differs: $(head -c 200 cpio/out/x.$n.err)"; fi
    if command -v bsdtar >/dev/null; then rm -rf cpio/x; mkdir cpio/x
      # bsdtar makes an empty file of a socket
      if bsdtar -xp --numeric-owner -C cpio/x -f $img 2> cpio/out/b.$n.err && cpio_cmp b.$n cpio/x tree.ref $from "^/dev/sock	"
      then ok "write cpio $n: bsdtar extracts it identically"; else bad "write cpio $n: bsdtar extraction differs: $(head -c 200 cpio/out/b.$n.err)"; fi; fi
    if command -v busybox >/dev/null; then rm -rf cpio/x; mkdir cpio/x
      # busybox sets neither the owner of a symbolic link nor the time of a directory it made before its entry came
      cpio_dec $img | (cd cpio/x && busybox cpio -idmu 2> ../out/bb.$n.err)
      r=$(python3 scan.py cpio/x | cut -f1-8 | tail -n +$from | grep -v "	[ld]	" | cmp - <(cut -f1-8 tree.ref | tail -n +$from | grep -v "	[ld]	") 2>&1)
      [ -z "$r" ] && ok "write cpio $n: busybox extracts it identically" || bad "write cpio $n: busybox extraction differs: $r"; fi
    rm -rf cpio/x
  }
  cw newc.cpio comp=none; cw newc.cpio.gz comp=gzip; cw newc.cpio.xz comp=xz; command -v zstd >/dev/null && cw newc.cpio.zst comp=zstd
  cw crc.cpio.gz comp=gzip cpioformat=crc; cw dot.cpio comp=none prefix=./ root=0
  xz -t cpio/img/w_newc.cpio.xz 2>/dev/null && [ "$(xz -l --robot cpio/img/w_newc.cpio.xz | awk '$1=="totals"{print $2}')" = 1 ] \
    && ok "the .cpio.xz is one xz stream with CRC32 (as the kernel needs)" || bad "the .cpio.xz is not a single xz stream"
  if [ -f cpio/img/w_crc.cpio.gz ]; then rm -rf cpio/x; mkdir cpio/x
    (gzip -dc cpio/img/w_crc.cpio.gz | (cd cpio/x && cpio -i --only-verify-crc --quiet)) > cpio/out/crc.err 2>&1
    [ ! -s cpio/out/crc.err ] && ok "GNU cpio confirms the checksums of the crc archive" || bad "crc checksums: $(head -c 200 cpio/out/crc.err)"; rm -rf cpio/x; fi
  # A cpio archive opened and saved again keeps its format, compression and names.
  if $F convert cpio/img/noprefix.cpio cpio/img/w_again.cpio verify=content 2> cpio/out/again.err && $F dump cpio/img/w_again.cpio 2>&1 >/dev/null | grep -q "header=Newc comp=None .*prefix='' root=False"
  then ok "a cpio archive saved again keeps its format"; else bad "cpio saved again: $(head -c 200 cpio/out/again.err) $($F dump cpio/img/w_again.cpio 2>&1 >/dev/null | grep cpio)"; fi
  $F convert cpio/img/newc.cpio.gz cpio/img/w_cpio.sqfs verify=content 2> cpio/out/c2s.err && $F dump cpio/img/w_cpio.sqfs 2>/dev/null | cut -f1-8 | cmp -s - <(cut -f1-8 tree.ref) \
    && ok "cpio -> SquashFS" || bad "cpio -> SquashFS: $(head -c 200 cpio/out/c2s.err)"
}

cpio_qemu() {
  [ -n "${CPIO_KERNEL:-}" ] && [ -n "${CPIO_BUSYBOX:-}" ] || { echo "  --    kernel boot skipped (set CPIO_KERNEL and CPIO_BUSYBOX)"; return; }
  command -v qemu-system-x86_64 >/dev/null || { echo "  --    kernel boot skipped: qemu-system-x86_64 is not installed"; return; }
  echo "cpio: the kernel boots the archives FsForge writes"
  # The tree, plus a static busybox and an /init that lists what the kernel unpacked on the serial console.
  rm -rf cpio/k; mkdir cpio/k; (cd tree && tar -c --exclude=./dev/sock . 2>/dev/null) | tar -xp -C cpio/k
  mkdir cpio/k/kbin; cp "$CPIO_BUSYBOX" cpio/k/kbin/busybox; mknod cpio/k/dev/console c 5 1
  cat > cpio/k/init <<'SH'
#!/kbin/busybox sh
B=/kbin/busybox
$B find / -xdev | while IFS= read -r p; do
  case "$p" in /|/init|/root|/kbin|/kbin/*|/dev/console) continue;; esac
  s=$($B stat -c '%f|%a|%u|%g|%Y|%s|%h|%t:%T' "$p")
  if [ -L "$p" ]; then h="L:$($B readlink "$p")"; elif [ -f "$p" ]; then h=$($B sha256sum "$p" | $B cut -d' ' -f1); else h=-; fi
  echo "@@E $p|$s|$h"
done
$B poweroff -f
SH
  chmod 755 cpio/k/init
  cat > cpio/expect.py <<'PY'
import os, sys, stat, hashlib
root = os.fsencode(sys.argv[1]).rstrip(b'/')
out = []
for d, dirs, files in os.walk(root):
    for n in dirs + files:
        p = os.path.join(d, n); rel = p[len(root):]
        if rel in (b'/init', b'/kbin', b'/dev/console') or rel.startswith(b'/kbin/'): continue
        st = os.lstat(p); m = st.st_mode; isdir = stat.S_ISDIR(m)
        dev = '%x:%x' % (os.major(st.st_rdev), os.minor(st.st_rdev)) if stat.S_ISCHR(m) or stat.S_ISBLK(m) else '0:0'
        if stat.S_ISLNK(m): h = b'L:' + os.readlink(p)
        elif stat.S_ISREG(m): h = hashlib.sha256(open(p, 'rb').read()).hexdigest().encode()
        else: h = b'-'
        out.append(rel + ('|%x|%o|%d|%d|%d|%s|%s|%s|' % (m, stat.S_IMODE(m), st.st_uid, st.st_gid, int(st.st_mtime),
            '-' if isdir else st.st_size, '-' if isdir else st.st_nlink, dev)).encode() + h)
sys.stdout.buffer.write(b'\n'.join(sorted(out)) + b'\n')
PY
  cat > cpio/got.py <<'PY'
import sys
lines = []
for raw in open(sys.argv[1], 'rb').read().split(b'\n'):
    i = raw.find(b'@@E ')
    if i < 0: continue
    f = raw[i + 4:].rstrip(b'\r').rsplit(b'|', 9)            # a name may hold '|'
    if (int(f[1], 16) & 0o170000) == 0o040000: f[6] = b'-'; f[7] = b'-'   # size and links of a directory are the filesystem's
    lines.append(b'|'.join(f))
sys.stdout.buffer.write(b'\n'.join(sorted(lines)) + b'\n')
PY
  python3 cpio/expect.py cpio/k > cpio/out/k.expect
  mksquashfs cpio/k cpio/img/k.sqfs -noappend -quiet -no-progress >/dev/null 2>&1
  kb() { # name options... : convert, boot, compare
    local n=$1; shift; local img=cpio/img/k_$n
    if ! $F convert cpio/img/k.sqfs $img format=cpio verify=content "$@" 2> cpio/out/k.$n.err; then bad "boot cpio $n: $(cat cpio/out/k.$n.err)"; return; fi
    timeout 600 qemu-system-x86_64 -m 1024 -nographic -no-reboot -kernel "$CPIO_KERNEL" -initrd $img \
      -append "console=ttyS0 panic=-1 loglevel=3" > cpio/out/boot.$n.log 2>&1
    python3 cpio/got.py cpio/out/boot.$n.log > cpio/out/k.$n.got
    if cmp -s cpio/out/k.expect cpio/out/k.$n.got; then ok "the kernel boots the $n initramfs and unpacks $(wc -l < cpio/out/k.$n.got) objects identically"
    else bad "boot cpio $n: $(grep -a -i "initramfs\|panic" cpio/out/boot.$n.log | head -2)"; diff cpio/out/k.expect cpio/out/k.$n.got | head -4 | cut -c1-200; fi
  }
  kb newc.cpio comp=none; kb newc.cpio.gz comp=gzip; kb newc.cpio.xz comp=xz; kb newc.cpio.zst comp=zstd
  kb crc.cpio.gz comp=gzip cpioformat=crc prefix=./ root=0
}
