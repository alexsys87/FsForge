#!/bin/bash
# Cross-checks the FsForge core against the reference Linux tools.
# Needs root (loop mounts, device nodes) and: squashfs-tools, e2fsprogs, tar, attr, python3;
# optional: libarchive-tools (bsdtar), busybox, acl.
#
#   sudo tests/verify-linux.sh [work-dir]
#
# Every image FsForge writes is mounted by the kernel and listed, unpacked with unsquashfs or tar,
# checked with e2fsck, and compared entry by entry (names, types, modes, owners, times, content
# hashes, hard links, extended attributes) with the source tree. Images made by mksquashfs,
# mke2fs and tar are read by FsForge and compared the same way.
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
F="$HERE/../tools/FsForge.Cli/bin/Release/net10.0/fsforge-cli"
W=${1:-/tmp/fsforge-verify}
[ -x "$F" ] || { echo "build the CLI first: dotnet build tools/FsForge.Cli -c Release"; exit 2; }
[ "$(id -u)" = 0 ] || { echo "run as root"; exit 2; }
rm -rf "$W"; mkdir -p "$W/mnt" "$W/img" "$W/out"; cd "$W"
PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); echo "  ok    $*"; }
bad()  { FAIL=$((FAIL+1)); echo "  FAIL  $*"; }

# ---------------------------------------------------------------- listing
cat > scan.py <<'PY'
import os, sys, hashlib, stat
root = os.fsencode(sys.argv[1]).rstrip(b'/')
def esc(b):
    return ''.join(chr(c) if 0x20 < c < 0x7f and c != 0x5c else '\\x%02x' % c for c in b)
items = []
def walk(d, rel):
    st = os.lstat(d)
    items.append((rel if rel else b'/', d, st))
    if stat.S_ISDIR(st.st_mode):
        for n in os.listdir(d):
            walk(d + b'/' + n, rel + b'/' + n)
walk(root, b'')
items.sort(key=lambda x: x[0])
first, hashes = {}, {}
for rel, p, st in items:
    m = st.st_mode
    t = 'd' if stat.S_ISDIR(m) else 'f' if stat.S_ISREG(m) else 'l' if stat.S_ISLNK(m) else 'c' if stat.S_ISCHR(m) else 'b' if stat.S_ISBLK(m) else 'p' if stat.S_ISFIFO(m) else 's'
    hl, key = '-', (st.st_dev, st.st_ino)
    if t != 'd' and st.st_nlink > 1:
        hl = first.setdefault(key, esc(rel))
    if t == 'f':
        if key not in hashes:
            h = hashlib.sha256()
            with open(p, 'rb') as f:
                for b in iter(lambda: f.read(1 << 20), b''): h.update(b)
            hashes[key] = h.hexdigest()
        extra = '%d:%s' % (st.st_size, hashes[key])
    elif t == 'l': extra = esc(os.readlink(p))
    elif t in 'cb': extra = '%d:%d' % (os.major(st.st_rdev), os.minor(st.st_rdev))
    else: extra = '-'
    mode = '0777' if t == 'l' else '%04o' % (m & 0o7777)
    xa = ''
    try: names = sorted(os.listxattr(p, follow_symlinks=False))
    except OSError: names = []
    for n in names:
        try: xa += n + '=' + os.getxattr(p, n, follow_symlinks=False).hex() + ';'
        except OSError: pass
    sys.stdout.write('%s\t%s\t%s\t%d\t%d\t%d\t%s\t%s\t%s\n' % (esc(rel), t, mode, st.st_uid, st.st_gid, int(st.st_mtime), extra, hl, xa or '-'))
PY

# -------------------------------------------------------------- test tree
mktree() {
  T=$1; small=$2; rm -rf $T; mkdir -p $T; pushd $T >/dev/null
  mkdir -p etc/config bin lib/modules/5.15/kernel usr/share/doc dev var/empty "weird names/sub dir" deep/a/b/c/d/e/f/g/h
  cp -a /etc/services /etc/passwd /etc/group /etc/hostname etc/ 2>/dev/null
  cp /bin/ls /bin/cat /bin/bash bin/
  ln -s bash bin/sh; ln -s /proc/mounts etc/mtab; ln -s ../../bin/ls "weird names/link to ls"
  ln bin/cat bin/cat-hardlink; ln bin/cat usr/share/cat-third
  ln -s $(python3 -c "print('a/'*100+'target')") etc/longlink
  mknod dev/null c 1 3; mknod dev/sda b 8 0; mknod dev/ttyS300 c 4 300; mknod dev/bigminor b 259 70000; mkfifo dev/fifo
  python3 -c "import socket; s=socket.socket(socket.AF_UNIX); s.bind('dev/sock')"
  : > var/empty/zero-length
  head -c 1 /dev/urandom > one-byte
  for n in 4095 4096 4097 131072 262144 1048576 1048577; do head -c $n /dev/urandom > f$n; done
  head -c 262145 /dev/zero > z256k1
  (cat /bin/bash; cat /bin/bash; head -c 777 /dev/urandom) > bigtext
  cp bigtext bigtext-dup; cp etc/services services-dup
  dd if=/dev/urandom of=sparse1 bs=4096 count=3 seek=300 conv=notrunc 2>/dev/null; truncate -s 3M sparse1
  truncate -s 5M sparse-all-zero
  dd if=/dev/urandom of=sparse2 bs=1000 count=1 2>/dev/null; dd if=/dev/urandom of=sparse2 bs=1000 count=1 seek=2500 conv=notrunc 2>/dev/null
  python3 - <<'PY'
import os
with open('fragmented','wb') as f:
    for i in range(700):
        f.seek(i*2*4096); f.write(os.urandom(4096))
for i in range(1, 601):
    open('usr/share/doc/file-%d.txt' % i, 'w').write('file number %d with some text\n' % i)
for i in range(1, 41):
    os.makedirs('lib/modules/5.15/kernel/d%d' % i)
    open('lib/modules/5.15/kernel/d%d/mod%d.ko' % (i, i), 'wb').write(os.urandom(i*511))
open(b'weird names/invalid-utf8-\xff\xfe.bin','wb').write(b'x')
open(b'weird names/'+b'n'*255,'wb').write(b'longname')
open(b'weird names/tab\there','wb').write(b't')
PY
  echo hi > "weird names/sub dir/файл на русском.txt"; echo hi > "weird names/日本語"; echo hi > "weird names/emoji-😀.txt"
  echo hi > "weird names/a:b*c?d<e>f|g\"h"; echo hi > "weird names/trailing dot."; echo hi > "weird names/CON"
  echo up > "weird names/Makefile"; echo low > "weird names/makefile"
  echo deep > deep/a/b/c/d/e/f/g/h/file
  chmod 4755 bin/ls; chmod 2750 etc/config; chmod 1777 var; chmod 000 one-byte; chmod 600 f4095; chmod 0444 etc/passwd
  chown 1000:1000 f4096; chown 65534:65534 f4097; chown 123456:654321 f131072; chown -h 1000:100 bin/sh; chown 33:33 usr/share/doc
  setfattr -n user.comment -v "hello world" etc/services
  setfattr -n user.bin -v 0x000102030405 f4096
  setfattr -n security.capability -v 0x0100000200200000000000000000000000000000 bin/cat
  setfattr -n security.selinux -v "u:object_r:system_file:s0" bin/ls
  setfattr -n security.selinux -v "u:object_r:system_file:s0" etc
  setfattr -n trusted.overlay.opaque -v y var
  [ $small = 0 ] && setfattr -n user.big -v "$(head -c 3000 /dev/zero | tr '\0' 'v')" bigtext
  touch -d "2001-02-03 04:05:06 UTC" etc/passwd; touch -h -d "2010-10-10 10:10:10 UTC" bin/sh; touch -d "2030-01-01 00:00:00 UTC" f4096
  touch -d "1999-12-31 23:59:59 UTC" var/empty deep "weird names"; touch -d "2020-05-05 05:05:05 UTC" .
  popd >/dev/null
}
mktree tree 0; python3 scan.py tree > tree.ref
mktree tree1k 1; python3 scan.py tree1k > tree1k.ref              # without the 3000-byte attribute (does not fit a 1K block)
grep -v "^/dev/sock" tree.ref > tree.tar.ref; cut -f1-8 tree.tar.ref > tree.tar.nox.ref

cmpdump() { # name image ref  : FsForge reads an image made by a reference tool
  local id=${1//[ \/]/_}
  if $F dump "$2" > "out/r.$id.txt" 2> "out/r.$id.err" && cmp -s "out/r.$id.txt" "$3"; then ok "read  $1"; else bad "read  $1: $(head -c 200 "out/r.$id.err")"; diff "out/r.$id.txt" "$3" | head -4 | cut -c1-200; fi
}
kmount() { # name image ref [filter] : the kernel reads an image written by FsForge
  if mount -o loop,ro "$2" mnt 2> out/m.$1.err; then python3 scan.py mnt | grep -v "${4:-^$}" > out/k.$1.txt; umount mnt
    cmp -s out/k.$1.txt "$3" && return 0; diff out/k.$1.txt "$3" | head -4 | cut -c1-200; return 1
  else cat out/m.$1.err; dmesg | tail -2; return 1; fi
}

# --------------------------------------------------------------- SquashFS
echo "SquashFS: images made by mksquashfs"
mk() { n=$1; shift; mksquashfs tree img/$n.sqfs -noappend -quiet -no-progress "$@" >/dev/null 2>&1 && cmpdump "squashfs $n" img/$n.sqfs tree.ref; }
mk gzip -comp gzip; mk gzip4k -comp gzip -b 4096; mk xz -comp xz; mk xzbcj -comp xz -Xbcj x86; mk lz4 -comp lz4; mk lz4hc -comp lz4 -Xhc
mk zstd -comp zstd; mk lzo -comp lzo; mk lzma -comp lzma 2>/dev/null; mk nofrag -comp gzip -no-fragments; mk nocomp -comp gzip -noI -noD -noF -noX
mk noexp -comp xz -no-exports; mk b1m -comp zstd -b 1048576

echo "SquashFS: images written by FsForge"
sq() { n=$1; src=$2; shift 2
  if ! $F convert $src img/w_$n.sqfs format=squashfs verify=content "$@" 2> out/w.$n.err; then bad "write squashfs $n: $(cat out/w.$n.err)"; return; fi
  kmount "sq_$n" img/w_$n.sqfs tree.ref && r1=1 || r1=0
  rm -rf uq; if unsquashfs -q -n -d uq img/w_$n.sqfs >/dev/null 2>out/u.$n.err; then python3 scan.py uq | cut -f1-5,7- | grep -v "^/dev/sock\|^/	" > out/u.$n.txt
    cut -f1-5,7- tree.ref | grep -v "^/dev/sock\|^/	" | cmp -s - out/u.$n.txt && r2=1 || r2=0; else r2=0; fi; rm -rf uq
  [ $r1 = 1 ] && [ $r2 = 1 ] && ok "write squashfs $n ($(stat -c %s img/w_$n.sqfs) bytes; kernel + unsquashfs)" || bad "write squashfs $n: kernel=$r1 unsquashfs=$r2"
}
sq gzip img/xz.sqfs comp=gzip; sq gzip4k img/xz.sqfs comp=gzip block=4096; sq xz img/gzip.sqfs comp=xz; sq xz1m img/gzip.sqfs comp=xz block=1048576
sq xzbcj img/gzip.sqfs comp=xz bcj=x86; sq xzfast img/gzip.sqfs comp=xz effort=0; sq lz4 img/xz.sqfs comp=lz4; sq zstd img/xz.sqfs comp=zstd
sq nofrag img/xz.sqfs comp=gzip nofrag=1; sq nodedup img/xz.sqfs comp=gzip dedup=0; sq fromlzo img/lzo.sqfs; [ -f img/lzma.sqfs ] && sq fromlzma img/lzma.sqfs

# -------------------------------------------------------------------- ext
echo "ext: images made by mke2fs"
me() { n=$1; t=$2; ref=$3; size=$4; shift 4; rm -f img/$n.img
  if mke2fs -q -F "$@" -d $t img/$n.img $size >/dev/null 2>out/mke.$n.err; then
    mount -o loop,ro img/$n.img mnt && { python3 scan.py mnt > out/eref.$n.txt; umount mnt; cmpdump "$n" img/$n.img out/eref.$n.txt; }
  else echo "  skip  $n (mke2fs: $(head -c 80 out/mke.$n.err))"; fi; }
me ext2 tree tree.ref 64M -t ext2; me ext2_1k tree1k tree1k.ref 40M -t ext2 -b 1024 -I 128; me ext2_rev0 tree1k tree1k.ref 40M -t ext2 -r 0 -b 1024
me ext3 tree1k tree1k.ref 64M -t ext3 -b 2048; me ext4 tree tree.ref 64M -t ext4; me ext4_1k tree1k tree1k.ref 48M -t ext4 -b 1024
me ext4_128 tree tree.ref 64M -t ext4 -I 128 -O ^has_journal; me ext4_nocsum tree tree.ref 64M -t ext4 -O ^metadata_csum,^64bit
me ext4_inline tree tree.ref 64M -t ext4 -O inline_data; me ext4_bigalloc tree tree.ref 64M -t ext4 -O bigalloc -C 16384
me ext4_eainode tree tree.ref 64M -t ext4 -O ea_inode; me ext4_metabg tree1k tree1k.ref 96M -t ext4 -b 1024 -O meta_bg,^resize_inode

echo "ext: images written by FsForge"
ex() { n=$1; src=$2; ref=$3; shift 3
  if ! $F convert $src img/w_$n.img verify=content "$@" 2> out/w.$n.err; then bad "write $n: $(cat out/w.$n.err)"; return; fi
  e2fsck -f -n img/w_$n.img > out/fsck.$n.txt 2>&1 && r0=1 || r0=0
  kmount "ext_$n" img/w_$n.img $ref "^/lost+found" && r1=1 || r1=0
  [ $r0 = 1 ] && [ $r1 = 1 ] && ok "write $n ($(stat -c %s img/w_$n.img) bytes; e2fsck + kernel)" || { bad "write $n: e2fsck=$r0 kernel=$r1"; grep -v "^Pass\|^e2fsck" out/fsck.$n.txt | head -6; }
}
mksquashfs tree1k img/tree1k.sqfs -noappend -quiet -no-progress -comp lz4 >/dev/null
for k in ext4 ext3 ext2; do ex $k img/xz.sqfs tree.ref format=$k; done
for k in ext4 ext3 ext2; do for bs in 1024 2048; do ex ${k}_$bs img/tree1k.sqfs tree1k.ref format=$k bs=$bs; done; done
ex ext4_i128 img/xz.sqfs tree.ref format=ext4 isize=128; ex ext2_1k_i128 img/tree1k.sqfs tree1k.ref format=ext2 bs=1024 isize=128
ex ext4_nojournal img/xz.sqfs tree.ref format=ext4 journal=0; ex ext4_64m img/xz.sqfs tree.ref format=ext4 size=64
ex ext4_200m_1k img/tree1k.sqfs tree1k.ref format=ext4 bs=1024 size=200; ex ext2_300m img/xz.sqfs tree.ref format=ext2 size=300
ex ext4_free img/xz.sqfs tree.ref format=ext4 free=20 label=rootfs reserved=5
for v in ext2 ext3 ext4 ext4_inline ext4_bigalloc ext4_metabg; do [ -f img/$v.img ] && { grep -v "^/lost+found" out/eref.$v.txt > out/eref.nolf.$v.txt; ex "same_$v" img/$v.img out/eref.nolf.$v.txt; }; done
if $F convert img/xz.sqfs img/w_small.img format=ext4 size=4 2>/dev/null; then bad "an image that is too small was accepted"; else ok "content that does not fit is rejected"; fi

# -------------------------------------------------------------------- tar
echo "tar: archives made by GNU tar${BSD:+ and bsdtar}"
mt() { n=$1; ref=$2; shift 2; (cd tree && tar -c "$@" -f ../img/$n . 2>/dev/null)
  if [ "$ref" = nox ]; then $F dump img/$n 2>out/r.$n.err | cut -f1-8 | cmp -s - tree.tar.nox.ref && ok "read  tar $n" || bad "read  tar $n: $(head -c 200 out/r.$n.err)"; else cmpdump "tar $n" img/$n tree.tar.ref; fi; }
X="--xattrs --xattrs-include=*"
mt gnu.tar nox --format=gnu; mt gnu_sparse.tar nox --format=gnu --sparse; mt oldgnu.tar nox --format=oldgnu
mt pax.tar x --format=pax $X; mt pax_sparse10.tar x --format=pax --sparse $X; mt pax_sparse01.tar x --format=pax --sparse --sparse-version=0.1 $X
mt pax_sparse00.tar x --format=pax --sparse --sparse-version=0.0 $X; mt pax.tar.gz x --format=pax $X -z; mt pax.tar.xz x --format=pax $X -J
command -v zstd >/dev/null && mt pax.tar.zst x --format=pax $X --zstd
if command -v bsdtar >/dev/null; then
  (cd tree && bsdtar -c --format=pax --xattrs -f ../img/bsd_pax.tar . 2>/dev/null); cmpdump "tar bsd_pax.tar" img/bsd_pax.tar tree.tar.ref
  (cd tree && bsdtar -c --format=gnutar -f ../img/bsd_gnu.tar . 2>/dev/null); $F dump img/bsd_gnu.tar 2>/dev/null | cut -f1-8 | cmp -s - tree.tar.nox.ref && ok "read  tar bsd_gnu.tar" || bad "read  tar bsd_gnu.tar"
fi

echo "tar: archives written by FsForge"
tw() { n=$1; shift; nox=0; case "$*" in *gnu*) nox=1;; esac
  if ! $F convert img/xz.sqfs img/w_$n format=tar verify=content "$@" 2> out/w.$n.err; then bad "write tar $n: $(cat out/w.$n.err)"; return; fi
  rm -rf tx; mkdir tx; r1=0
  if tar -xp --xattrs --xattrs-include='*' --numeric-owner -C tx -f img/w_$n 2>out/x.$n.err; then
    if [ $nox = 1 ]; then python3 scan.py tx | cut -f1-8 | cmp -s - tree.tar.nox.ref && r1=1; else python3 scan.py tx | cmp -s - tree.tar.ref && r1=1; fi; fi
  rm -rf tx; [ $r1 = 1 ] && ok "write tar $n ($(stat -c %s img/w_$n) bytes; GNU tar)" || bad "write tar $n: GNU tar extraction differs: $(head -c 200 out/x.$n.err)"
}
tw pax.tar comp=none; tw pax.tar.gz comp=gzip; tw pax.tar.xz comp=xz; tw pax.tar.zst comp=zstd; tw gnu.tar comp=none dialect=gnu; tw gnu.tar.gz comp=gzip dialect=gnu
tw noprefix.tar comp=none prefix=
xz -t img/w_pax.tar.xz 2>/dev/null && ok "xz accepts the .tar.xz" || bad "xz rejects the .tar.xz"
gzip -t img/w_pax.tar.gz 2>/dev/null && ok "gzip accepts the .tar.gz" || bad "gzip rejects the .tar.gz"
if command -v bsdtar >/dev/null; then rm -rf tx; mkdir tx; bsdtar -xp --xattrs --numeric-owner -C tx -f img/w_pax.tar.gz 2>/dev/null
  python3 scan.py tx | tail -n +2 | cmp -s - <(tail -n +2 tree.tar.ref) && ok "bsdtar extracts the .tar.gz identically" || bad "bsdtar extraction differs"; rm -rf tx; fi
if command -v busybox >/dev/null; then rm -rf tx; mkdir tx; busybox tar -xzp -C tx -f img/w_gnu.tar.gz 2>/dev/null
  # busybox restores neither the times of directories and links nor the owners of links
  python3 scan.py tx | cut -f1-5,7-8 | grep -v "	l	0777	" | cmp -s - <(cut -f1-5,7-8 tree.tar.nox.ref | grep -v "	l	0777	") && ok "busybox extracts the .tar.gz identically" || bad "busybox extraction differs"; rm -rf tx; fi

# ------------------------------------------------------ folders and ACLs
echo "folders"
rm -rf hx; $F extract img/xz.sqfs hx 2>out/hx.err && $F pack hx img/w_repacked.sqfs comp=lz4 2>>out/hx.err && $F dump img/w_repacked.sqfs 2>/dev/null | cmp -s - tree.ref \
  && ok "extract to a folder + pack gives the same tree" || bad "extract + pack differs: $(tail -2 out/hx.err)"
if command -v setfacl >/dev/null; then
  rm -rf acl; mkdir -p acl/d; echo x > acl/f; setfacl -m u:1000:rw,g:50:r,m::rw acl/f 2>/dev/null; setfacl -d -m u:33:rwx acl/d 2>/dev/null
  touch -d "1960-01-01 UTC" acl/f; touch -d "2100-01-01 UTC" acl/d; touch -d "2020-01-01 UTC" acl
  python3 scan.py acl > acl.ref
  (cd acl && tar -c --format=pax --acls --xattrs --xattrs-include='*' -f ../img/acl.tar .)
  mke2fs -q -F -t ext4 -d acl img/acl.img 16M >/dev/null 2>&1; mount -o loop,ro img/acl.img mnt; python3 scan.py mnt | grep -v "^/lost+found" > acl.ext.ref; umount mnt
  mount -o loop,ro img/acl.img mnt; python3 scan.py mnt > acl.full.ref; umount mnt
  cmpdump "ext4 with POSIX ACLs and times before 1970 and after 2038" img/acl.img acl.full.ref
  cmpdump "tar with POSIX ACLs and times before 1970 and after 2038" img/acl.tar acl.ref
  ex acl_ext4 img/acl.img acl.ext.ref format=ext4
fi

echo; echo "$PASS passed, $FAIL failed"; [ $FAIL = 0 ]
