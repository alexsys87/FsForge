# Vendor firmware checks. Sourced by verify-linux.sh, which provides $F (the CLI), tree/ with its listing tree.ref,
# img/xz.sqfs (the same tree), ok/bad and cmpdump; everything else lives in fw/.
#
#   fw_run      U-Boot uImage files made by mkimage (u-boot-tools) are opened at the filesystem they hold and must
#               read as the image itself: an OpenWrt-style sysupgrade image (an lzma kernel uImage padded to the
#               erase block, the SquashFS behind it, the JFFS2 marker), a JFFS2 root behind the kernel, a filesystem
#               uImage, a uInitrd with a GNU cpio archive in gzip, a gzipped ext2 initrd, and a multi-file image
#               (kernel, ramdisk, device tree) whose parts are chosen with vol=. Each is then changed in place:
#               the header must have the new size and correct CRC-32 checksums (checked with zlib), dumpimage must
#               take the changed part out of it and the reference tool (unsquashfs, GNU cpio) unpack it with the change,
#               the kernel must stay byte for byte, and OpenWrt's 0xdeadc0de marker must sit on the first erase block
#               boundary behind the new SquashFS. The filesystem alone is only written under another name.
#               FIT images made by mkimage -f from an .its (needs dtc): data in the tree, outside it (-E) and at a fixed
#               position (-p), with crc32, sha1 and sha256 hashes, a PetaLinux image.ub whose ramdisk is a uImage, and
#               an OpenWrt .itb with the SquashFS behind it. Each image is opened with vol=<node name> and must read
#               as the image file it was made from; the hashes mkimage computed must be found right, and a changed
#               byte must be reported. Each FIT image is then changed in place, at its ramdisk and at its root
#               filesystem: dtc must take the new tree, pylibfdt (python3-libfdt, U-Boot's own library; skipped
#               without it) must find every hash right, dumpimage must take out the same kernel and the changed image,
#               and GNU cpio / unsquashfs must see the change, also in the ramdisk of image.ub inside its uImage.
#               Broadcom TRX images (version 1 with loader, kernel and the root filesystem at 64 KiB and the JFFS2
#               marker; version 2; the zlib form of the CRC) and a Netgear CHK image holding one, made by a Python
#               script after OpenWrt's trx.h with the SquashFS of mksquashfs, must read as that SquashFS; a changed
#               byte must be reported by the CRC. Each is then changed in place: Python checks the new CRC (with the
#               bin-header of version 2 counted as CFE counts it) and the CHK checksums (as mkchkimg makes them), and
#               unsquashfs unpacks the new root filesystem. With TRX=<trx of OpenWrt's firmware-utils> the images are
#               also made by trx (version 1, -F, version 2 with a bin-header), and after the change each must be byte
#               for byte what trx makes from the same parts and the new filesystem.
#               With MKCHKIMG=<mkchkimg> the TRX is also wrapped by mkchkimg (its default board, and another board
#               and region); after the change the CHK header must be byte for byte what mkchkimg makes. mkchkimg also
#               makes CHK images without a TRX (a uImage kernel and the SquashFS as -k and -f; an OpenWrt sysupgrade
#               image as -k): they must read as the SquashFS and, after the change, be what mkchkimg makes.
#               TP-Link images (the classic header of mktplinkfw: cut with -s, padded to the flash layout, with the
#               JFFS2 markers, combined; safeloader factory images with their partition table) made by a Python script
#               after OpenWrt's mktplinkfw.c and tplink-safeloader.c, and with MKTPLINKFW=<mktplinkfw> (also -a),
#               MKTPLINKFW2=<mktplinkfw2> (header versions 2 and 3: -s, padded, -j, -a, -c) and
#               TPLINK_SAFELOADER=<tplink-safeloader> by those tools themselves, must read as the SquashFS of
#               mksquashfs, with their MD5 found right; a changed byte must be reported. Each is then changed in place:
#               Python checks the new MD5 with the key, unsquashfs unpacks the new root filesystem, and with the tools
#               set, the result must be byte for byte what the tool makes from the same kernel and the new filesystem.
#               With FWTOOL=<fwtool of an OpenWrt build, staging_dir/host/bin/fwtool> (or fwtool in PATH) the
#               sysupgrade image also gets metadata and a signature block from fwtool -I / -S; after FsForge changes the
#               root filesystem, fwtool -i and -s (which check the trailer checksums) must take out the same files.

fw_run() {
  if ! command -v mkimage >/dev/null; then echo "uImage: skipped (u-boot-tools with mkimage is not installed)"; return; fi
  echo "uImage made by mkimage"
  rm -rf fw; mkdir -p fw
  head -c 600000 /dev/urandom > fw/kernel.bin
  mkimage -A mips -O linux -T kernel -C lzma -a 0x80060000 -e 0x80060000 -n "OpenWrt Linux-6.6.73" -d fw/kernel.bin fw/kernel.uImage > /dev/null
  python3 - fw/kernel.uImage img/xz.sqfs fw/sysupgrade.bin <<'PY'
import sys
k = open(sys.argv[1], 'rb').read(); r = open(sys.argv[2], 'rb').read()
k += b'\xff' * (-len(k) % 65536); r += b'\x00' * (-len(r) % 65536)
open(sys.argv[3], 'wb').write(k + r + b'\xde\xad\xc0\xde')
PY
  cmpdump "uImage: sysupgrade image, SquashFS behind the kernel" fw/sysupgrade.bin tree.ref
  $F info fw/sysupgrade.bin > fw/sys.info 2>&1
  grep -q "data CRC ok" fw/sys.info && grep -q '^  \* rootfs: SquashFS' fw/sys.info && grep -q "kernel: kernel (lzma)" fw/sys.info \
    && ok "uImage: header, data CRC and parts as mkimage made them" || bad "uImage info: $(head -c 400 fw/sys.info)"

  mkimage -A arm -O linux -T filesystem -C none -n rootfs -d img/xz.sqfs fw/fs.uImage > /dev/null
  cmpdump "uImage: filesystem image" fw/fs.uImage tree.ref

  if command -v mkfs.jffs2 >/dev/null; then
    mkfs.jffs2 -r tree -o fw/root.jffs2 -e 65536 -p 2>/dev/null
    python3 -c "import sys; k=open('fw/kernel.uImage','rb').read(); k+=b'\xff'*(-len(k)%65536); open('fw/jffs2.bin','wb').write(k+open('fw/root.jffs2','rb').read())"
    $F dump fw/root.jffs2 > fw/jffs2.ref 2>/dev/null
    cmpdump "uImage: JFFS2 behind the kernel" fw/jffs2.bin fw/jffs2.ref
  fi

  if command -v cpio >/dev/null; then
    (cd tree && find . | LC_ALL=C sort | cpio -o -H newc 2>/dev/null) | gzip -9 > fw/initrd.cpio.gz
    $F dump fw/initrd.cpio.gz > fw/initrd.ref 2>/dev/null
    mkimage -A arm -O linux -T ramdisk -C gzip -n initramfs -d fw/initrd.cpio.gz fw/uInitrd > /dev/null
    cmpdump "uImage: uInitrd with a GNU cpio archive in gzip" fw/uInitrd fw/initrd.ref
    printf '\xd0\x0d\xfe\xed' > fw/board.dtb; head -c 3001 /dev/urandom >> fw/board.dtb
    mkimage -A arm -O linux -T multi -C none -n multi -d fw/kernel.bin:fw/initrd.cpio.gz:fw/board.dtb fw/multi.uImage > /dev/null
    cmpdump "uImage: multi-file image, the ramdisk by default" fw/multi.uImage fw/initrd.ref
    if $F dump fw/multi.uImage vol=1 2>/dev/null | cmp -s - fw/initrd.ref; then ok "uImage: part chosen by its number in mkimage -l"; else bad "multi.uImage vol=1"; fi
    if $F info fw/multi.uImage vol=fdt > /dev/null 2> fw/fdt.err; then bad "the device tree was opened"; else grep -q "device tree" fw/fdt.err && ok "uImage: a device tree is not opened" || bad "$(cat fw/fdt.err)"; fi
  fi

  if command -v mke2fs >/dev/null; then
    mke2fs -q -F -t ext2 -d tree fw/ramdisk.ext2 64M > /dev/null 2>&1 && gzip -9 -k fw/ramdisk.ext2
    $F dump fw/ramdisk.ext2 > fw/ext2.ref 2>/dev/null
    mkimage -A arm -O linux -T ramdisk -C gzip -n "ext2 initrd" -d fw/ramdisk.ext2.gz fw/ext2.uInitrd > /dev/null
    cmpdump "uImage: gzipped ext2 initrd" fw/ext2.uInitrd fw/ext2.ref
  fi

  if $F convert fw/sysupgrade.bin fw/rootfs.sqfs > /dev/null 2> fw/alone.err && command -v unsquashfs >/dev/null && unsquashfs -n -d fw/un fw/rootfs.sqfs > /dev/null 2>&1; then
    python3 scan.py fw/un | cmp -s - tree.ref && ok "uImage: the filesystem saved alone, unpacked by unsquashfs" || bad "the filesystem saved alone differs"
  else bad "save the filesystem alone: $(cat fw/alone.err)"; fi
  if $F convert fw/sysupgrade.bin fw/sysupgrade.bin format=tar 2> fw/tar.err; then bad "a tar archive was written over the firmware"
  else grep -q "Save under another name" fw/tar.err && ok "uImage: a tar archive is not written over the firmware" || bad "$(cat fw/tar.err)"; fi

  fw_fit
  fw_trx
  fw_tplink
  echo "uImage rebuilt by FsForge"
  head -c 300000 /dev/urandom > fw/big.bin
  cp fw/sysupgrade.bin fw/w_sys.bin
  if $F edit fw/w_sys.bin fw/w_sys.bin "add:/:fw/big.bin" > /dev/null 2> fw/w_sys.err; then
    fw_check fw/w_sys.bin kernel=fw/kernel.uImage && ok "sysupgrade: header, kernel and the marker behind the grown SquashFS" || bad "sysupgrade layout: $(cat fw/check.out)"
    python3 - fw/w_sys.bin fw/w_root.sqfs <<'PY'
import sys
d = open(sys.argv[1], 'rb').read(); at = d.index(b'hsqs')
open(sys.argv[2], 'wb').write(d[at:])
PY
    rm -rf fw/un2; unsquashfs -n -d fw/un2 fw/w_root.sqfs > /dev/null 2>&1 && cmp -s fw/un2/big.bin fw/big.bin && [ -e fw/un2/etc ] \
      && ok "sysupgrade: unsquashfs unpacks the new root filesystem" || bad "unsquashfs on the rebuilt sysupgrade image"
  else bad "edit sysupgrade: $(cat fw/w_sys.err)"; fi

  local fwtool=${FWTOOL:-$(command -v fwtool || true)}
  if [ -n "$fwtool" ] && [ -x "$fwtool" ]; then
    cp fw/sysupgrade.bin fw/w_meta.bin
    printf '{"metadata_version":"1.1","supported_devices":["vendor,board"]}' > fw/meta.json; head -c 64 /dev/urandom > fw/sig.bin
    "$fwtool" -I fw/meta.json fw/w_meta.bin && "$fwtool" -S fw/sig.bin fw/w_meta.bin
    if $F edit fw/w_meta.bin fw/w_meta.bin "add:/:fw/big.bin" > /dev/null 2> fw/w_meta.err; then
      rm -f fw/o.json fw/o.sig
      "$fwtool" -q -i fw/o.json fw/w_meta.bin && "$fwtool" -q -s fw/o.sig fw/w_meta.bin && cmp -s fw/o.json fw/meta.json && cmp -s fw/o.sig fw/sig.bin \
        && ok "fwtool: metadata and signature of the rebuilt sysupgrade image pass fwtool's checksum" || bad "fwtool rejects the rebuilt image"
    else bad "edit sysupgrade with fwtool blocks: $(cat fw/w_meta.err)"; fi
  else echo "  --    fwtool checks skipped: set FWTOOL to the fwtool of an OpenWrt build"; fi

  if [ -e fw/uInitrd ]; then
    cp fw/uInitrd fw/w_initrd; echo added > fw/added.txt
    if $F edit fw/w_initrd fw/w_initrd "add:/:fw/added.txt" > /dev/null 2> fw/w_initrd.err; then
      fw_check fw/w_initrd && ok "uInitrd: new size and checksums" || bad "uInitrd header: $(cat fw/check.out)"
      rm -f fw/x.gz; dumpimage -T ramdisk -p 0 -o fw/x.gz fw/w_initrd > /dev/null 2>&1
      gzip -dc fw/x.gz 2>/dev/null | cpio -t 2>/dev/null | grep -qx "added.txt" && ok "uInitrd: dumpimage + GNU cpio see the added file" || bad "uInitrd: the added file is not in the archive dumpimage takes out"
    else bad "edit uInitrd: $(cat fw/w_initrd.err)"; fi
    cp fw/multi.uImage fw/w_multi.uImage
    if $F edit fw/w_multi.uImage fw/w_multi.uImage "add:/:fw/added.txt" > /dev/null 2> fw/w_multi.err; then
      fw_check fw/w_multi.uImage && ok "multi-file: new size and checksums" || bad "multi-file header: $(cat fw/check.out)"
      rm -f fw/k.bin fw/r.gz fw/d.dtb
      dumpimage -T multi -p 0 -o fw/k.bin fw/w_multi.uImage > /dev/null 2>&1; dumpimage -T multi -p 1 -o fw/r.gz fw/w_multi.uImage > /dev/null 2>&1
      dumpimage -T multi -p 2 -o fw/d.dtb fw/w_multi.uImage > /dev/null 2>&1
      cmp -s fw/k.bin fw/kernel.bin && cmp -s fw/d.dtb fw/board.dtb && gzip -dc fw/r.gz 2>/dev/null | cpio -t 2>/dev/null | grep -qx "added.txt" \
        && ok "multi-file: dumpimage takes out the same kernel and device tree and the changed ramdisk" || bad "multi-file parts after the rebuild"
    else bad "edit multi-file: $(cat fw/w_multi.err)"; fi
  fi
  if [ -e fw/ext2.uInitrd ]; then
    cp fw/ext2.uInitrd fw/w_ext2.uInitrd
    if $F edit fw/w_ext2.uInitrd fw/w_ext2.uInitrd "add:/:fw/added.txt" > /dev/null 2> fw/w_ext2.err; then
      rm -f fw/e.gz fw/e.img; dumpimage -T ramdisk -p 0 -o fw/e.gz fw/w_ext2.uInitrd > /dev/null 2>&1; gzip -dc fw/e.gz > fw/e.img 2>/dev/null
      fw_check fw/w_ext2.uInitrd && e2fsck -fn fw/e.img > fw/e2fsck.out 2>&1 && debugfs -R "cat /added.txt" fw/e.img 2>/dev/null | grep -q added \
        && ok "ext2 initrd: gzipped again, e2fsck clean, the added file is there" || bad "ext2 initrd after the rebuild: $(tail -2 fw/e2fsck.out)"
    else bad "edit ext2 initrd: $(cat fw/w_ext2.err)"; fi
  fi
}

fw_check() { # file [kernel=<uImage the file must start with, padded>] : header and data CRC with zlib; the marker behind a SquashFS
  python3 - "$@" > fw/check.out 2>&1 <<'PY'
import sys, struct, zlib
d = open(sys.argv[1], 'rb').read()
h = bytearray(d[:64]); hcrc, size, dcrc = struct.unpack('>I', h[4:8])[0], struct.unpack('>I', h[12:16])[0], struct.unpack('>I', h[24:28])[0]
h[4:8] = bytes(4)
assert zlib.crc32(bytes(h)) == hcrc, 'header CRC'
assert 64 + size <= len(d) and zlib.crc32(d[64:64 + size]) == dcrc, 'data CRC'
for a in sys.argv[2:]:
    if a.startswith('kernel='):
        k = open(a[7:], 'rb').read()
        assert d[:len(k)] == k, 'kernel changed'
        at = d.index(b'hsqs', len(k))
        end = at + struct.unpack('<Q', d[at + 40:at + 48])[0]
        m = (end + 65535) // 65536 * 65536
        assert d[m:m + 4] == b'\xde\xad\xc0\xde', 'no 0xdeadc0de at 0x%x' % m
PY
}

fw_fit() {
  if ! command -v dtc >/dev/null; then echo "FIT: skipped (dtc, which mkimage -f needs, is not installed)"; return; fi
  echo "FIT images made by mkimage -f"
  [ -e fw/initrd.cpio.gz ] || { (cd tree && find . | LC_ALL=C sort | cpio -o -H newc 2>/dev/null) | gzip -9 > fw/initrd.cpio.gz; $F dump fw/initrd.cpio.gz > fw/initrd.ref 2>/dev/null; }
  gzip -9 -c fw/kernel.bin > fw/kernel.gz
  echo '/dts-v1/; / { model = "board"; compatible = "vendor,board"; };' | dtc -I dts -O dtb -o fw/board-real.dtb - 2>/dev/null
  cp img/xz.sqfs fw/root.sqfs
  mkimage -A arm -O linux -T ramdisk -C gzip -n petalinux-image -d fw/initrd.cpio.gz fw/rootfs.cpio.gz.u-boot > /dev/null
  fw_its() { # ramdisk-file : an .its with kernel, device tree, ramdisk and root filesystem
    cat <<ITS
/dts-v1/;
/ {
	description = "FsForge test FIT";
	#address-cells = <1>;
	images {
		kernel-1 { data = /incbin/("kernel.gz"); type = "kernel"; arch = "arm"; os = "linux"; compression = "gzip"; load = <0x80008000>; entry = <0x80008000>;
			hash-1 { algo = "crc32"; }; hash-2 { algo = "sha1"; }; };
		fdt-1 { data = /incbin/("board-real.dtb"); type = "flat_dt"; arch = "arm"; compression = "none"; hash-1 { algo = "sha256"; }; };
		ramdisk-1 { data = /incbin/("$1"); type = "ramdisk"; arch = "arm"; os = "linux"; compression = "none"; hash-1 { algo = "sha256"; }; };
		rootfs-1 { data = /incbin/("root.sqfs"); type = "filesystem"; arch = "arm"; os = "linux"; compression = "none"; hash-1 { algo = "crc32"; }; };
	};
	configurations { default = "config-1"; config-1 { kernel = "kernel-1"; fdt = "fdt-1"; ramdisk = "ramdisk-1"; }; };
};
ITS
  }
  fw_its initrd.cpio.gz > fw/fit.its; fw_its rootfs.cpio.gz.u-boot > fw/peta.its
  (cd fw && mkimage -f fit.its fit.itb > /dev/null && mkimage -E -f fit.its fit-ext.itb > /dev/null && mkimage -E -p 0x1000 -f fit.its fit-pos.itb > /dev/null \
     && mkimage -f peta.its image.ub > /dev/null) || { bad "mkimage -f failed"; return; }
  local f
  for f in fit fit-ext fit-pos image; do
    local file=fw/$f.itb; [ $f = image ] && file=fw/image.ub
    if $F dump $file vol=rootfs-1 2>/dev/null | cmp -s - tree.ref && $F dump $file vol=ramdisk-1 2>/dev/null | cmp -s - fw/initrd.ref; then
      ok "FIT $(basename $file): rootfs-1 and ramdisk-1 read as their image files"
    else bad "FIT $(basename $file): content differs"; fi
    $F info $file vol=rootfs-1 > fw/$f.info 2>&1
    grep -q "5 hash(es) checked, 0 wrong" fw/$f.info && ok "FIT $(basename $file): the hashes mkimage made are right" || bad "FIT hashes: $(grep -i hash fw/$f.info | head -2)"
  done
  grep -q "inside a uImage 'petalinux-image'" fw/image.info && ok "image.ub: the ramdisk inside its uImage is found" || bad "image.ub ramdisk: $(grep ramdisk fw/image.info)"
  python3 - fw/fit.itb fw/fit-bad.itb <<'PY'
import sys
d = bytearray(open(sys.argv[1], 'rb').read()); at = d.index(b'hsqs') + 2000; d[at] ^= 1
open(sys.argv[2], 'wb').write(d)
PY
  $F info fw/fit-bad.itb vol=rootfs-1 2> fw/fit-bad.err > /dev/null; grep -q "crc32 hash of image 'rootfs-1'.*does not match" fw/fit-bad.err \
    && ok "FIT: a changed byte of rootfs-1 is reported by its crc32" || bad "FIT changed byte: $(cat fw/fit-bad.err)"
  (cd fw && printf '/dts-v1/;\n/ { description = "OpenWrt"; images { kernel-1 { data = /incbin/("kernel.gz"); type = "kernel"; compression = "gzip"; hash-1 { algo = "sha1"; }; }; };\n configurations { default = "c"; c { kernel = "kernel-1"; }; }; };\n' > owrt.its \
     && mkimage -f owrt.its owrt.itb > /dev/null)
  python3 -c "import sys; k=open('fw/owrt.itb','rb').read(); k+=b'\xff'*(-len(k)%65536); open('fw/owrt-sysupgrade.itb','wb').write(k+open('fw/root.sqfs','rb').read())"
  cmpdump "FIT: OpenWrt .itb, SquashFS behind it" fw/owrt-sysupgrade.itb tree.ref
  fw_fit_rebuild

}

fw_fit_rebuild() {
  echo "FIT rebuilt by FsForge"
  local py=""; for p in python3 python3.12 python3.11; do command -v $p >/dev/null && $p -c 'import libfdt' 2>/dev/null && { py=$p; break; }; done
  cat > fw/fitcheck.py <<'PY'
# Every image of a FIT file with pylibfdt: its data (in the tree, at data-offset or data-position) and each hash.
import sys, libfdt, hashlib, zlib, struct
d = open(sys.argv[1], 'rb').read()
f = libfdt.Fdt(d[:struct.unpack('>I', d[4:8])[0]])
base = (f.totalsize() + 3) & ~3
node = f.first_subnode(f.path_offset('/images'), libfdt.QUIET_NOTFOUND)
bad = 0
while node >= 0:
    props, p = {}, f.first_property_offset(node, libfdt.QUIET_NOTFOUND)
    while p >= 0:
        prop = f.get_property_by_offset(p); props[prop.name] = bytes(prop); p = f.next_property_offset(p, libfdt.QUIET_NOTFOUND)
    if 'data' in props: data = props['data']
    else:
        at = base + struct.unpack('>I', props['data-offset'])[0] if 'data-offset' in props else struct.unpack('>I', props['data-position'])[0]
        data = d[at:at + struct.unpack('>I', props['data-size'])[0]]
    h = f.first_subnode(node, libfdt.QUIET_NOTFOUND)
    while h >= 0:
        if f.get_name(h).startswith('hash'):
            algo = bytes(f.getprop(h, 'algo')).rstrip(b'\0').decode()
            calc = struct.pack('>I', zlib.crc32(data)) if algo == 'crc32' else hashlib.new(algo, data).digest()
            if calc != bytes(f.getprop(h, 'value')): bad += 1; print(f.get_name(node), algo, 'wrong')
        h = f.next_subnode(h, libfdt.QUIET_NOTFOUND)
    node = f.next_subnode(node, libfdt.QUIET_NOTFOUND)
sys.exit(1 if bad else 0)
PY
  [ -n "$py" ] || echo "  --    pylibfdt checks skipped: install python3-libfdt"
  echo added > fw/added.txt
  local f v
  for f in fit.itb fit-ext.itb fit-pos.itb image.ub; do
    for v in ramdisk-1 rootfs-1; do
      cp fw/$f fw/w_$f
      if ! $F edit fw/w_$f fw/w_$f vol=$v "add:/:fw/added.txt" > /dev/null 2> fw/w_fit.err; then bad "edit $f $v: $(grep -v '^warning' fw/w_fit.err | head -2)"; continue; fi
      local good=1
      dtc -I dtb -O dts -o /dev/null fw/w_$f 2>/dev/null || good=0
      [ -z "$py" ] || $py fw/fitcheck.py fw/w_$f > fw/fitcheck.out 2>&1 || good=0
      rm -f fw/x.k fw/x.img; dumpimage -T flat_dt -p 0 -o fw/x.k fw/w_$f > /dev/null 2>&1; cmp -s fw/x.k fw/kernel.gz || good=0
      if [ $v = rootfs-1 ]; then
        dumpimage -T flat_dt -p 3 -o fw/x.img fw/w_$f > /dev/null 2>&1; rm -rf fw/xu; unsquashfs -n -d fw/xu fw/x.img > /dev/null 2>&1 && [ -e fw/xu/added.txt ] || good=0
      else
        dumpimage -T flat_dt -p 2 -o fw/x.img fw/w_$f > /dev/null 2>&1
        if [ $f = image.ub ]; then dumpimage -T ramdisk -p 0 -o fw/x.gz fw/x.img > /dev/null 2>&1; else cp fw/x.img fw/x.gz; fi
        gzip -dc fw/x.gz 2>/dev/null | cpio -t 2>/dev/null | grep -qx added.txt || good=0
      fi
      [ $good = 1 ] && ok "FIT $f rebuilt at $v: tree, hashes, kernel kept, change seen by the reference tools" || bad "FIT $f $v after the rebuild: $(cat fw/fitcheck.out 2>/dev/null)"
    done
  done
}

fw_trx() {
  echo "Broadcom TRX and Netgear CHK"
  cat > fw/trxlib.py <<'PY'
# TRX after OpenWrt's trx.c and Netgear CHK after mkchkimg: building and checking.
import struct, zlib
def netgear(data):
    c0 = c1 = 0
    for b in data: c0 = (c0 + b) & 0xffffffff; c1 = (c1 + c0) & 0xffffffff
    f0 = (c0 & 0xffff) + (c0 >> 16); f1 = (c1 & 0xffff) + (c1 >> 16)
    return ((((f1 >> 16) + f1) & 0xffff) << 16) | (((f0 >> 16) + f0) & 0xffff)
def trx(parts, version, aligns, marker=False, std=False):
    hs = 28 if version == 1 else 32
    body = bytearray(hs); offs = []
    for p, a in zip(parts, aligns):
        while len(body) % a: body.append(0)
        offs.append(len(body)); body += p
        while len(body) % 4: body.append(0)
    if marker:
        while len(body) % 65536: body.append(0xFF)
        body += b'\xde\xad\xc0\xde'
    while len(body) % 4096: body.append(0)
    slots = (hs - 16) // 4
    struct.pack_into('<IIIHH' + 'I' * slots, body, 0, 0x30524448, len(body), 0, 0, version, *(offs + [0] * (slots - len(offs))))
    c = crc(bytes(body), 0)
    struct.pack_into('<I', body, 8, (~c & 0xffffffff) if std else c)
    return bytes(body)
def crc(d, at):
    l = struct.unpack('<I', d[at + 4:at + 8])[0]; v = struct.unpack('<H', d[at + 14:at + 16])[0]
    cov = bytearray(d[at + 12:at + l])
    if v == 2:
        b = struct.unpack('<I', d[at + 28:at + 32])[0]
        if b: cov[b + 22 - 12:b + 30 - 12] = b'\xff' * 8
    return (~zlib.crc32(bytes(cov))) & 0xffffffff
def chk(t, board):
    h = bytearray(struct.pack('>II8xIIIIII', 0x2A23245E, 40 + len(board), netgear(t), netgear(b''), len(t), 0, netgear(t), 0) + board)
    struct.pack_into('>I', h, 36, netgear(h)); return bytes(h) + t
def check(path):
    d = open(path, 'rb').read(); at = 0; ok = True
    if d[:4] == b'*#$^':
        at = struct.unpack('>I', d[4:8])[0]; k, r = struct.unpack('>II', d[24:32])
        h = bytearray(d[:at]); hs = struct.unpack('>I', h[36:40])[0]; h[36:40] = bytes(4)
        ok = (struct.unpack('>I', d[16:20])[0] == netgear(d[at:at + k]) and struct.unpack('>I', d[32:36])[0] == netgear(d[at:at + k + r]) and hs == netgear(h))
    stored = struct.unpack('<I', d[at + 8:at + 12])[0]; c = crc(d, at)
    return ok and stored in (c, ~c & 0xffffffff)
PY
  python3 - fw img/xz.sqfs <<'PY'
import sys, os
sys.path.insert(0, sys.argv[1]); import trxlib as t
d, root = sys.argv[1], open(sys.argv[2], 'rb').read()
loader, kernel = os.urandom(3000), b'\x5d\x00\x00\x80\x00' + os.urandom(150000)
open(d + '/loader.bin', 'wb').write(loader); open(d + '/kernel.lzma', 'wb').write(kernel)
open(d + '/v1.trx', 'wb').write(t.trx([loader, kernel, root], 1, [4, 1024, 65536], marker=True))
open(d + '/v2.trx', 'wb').write(t.trx([kernel, root], 2, [4, 65536]))
open(d + '/std.trx', 'wb').write(t.trx([kernel, root], 1, [4, 4096], std=True))
open(d + '/netgear.chk', 'wb').write(t.chk(t.trx([loader, kernel, root], 1, [4, 1024, 65536]), b'U12H139T00_NETGEAR'))
b = bytearray(open(d + '/v1.trx', 'rb').read()); b[-100] ^= 1; open(d + '/bad.trx', 'wb').write(b)
PY
  local f
  for f in v1.trx v2.trx std.trx netgear.chk; do cmpdump "TRX: $f" fw/$f tree.ref; done
  $F info fw/netgear.chk 2>/dev/null | grep -q "CRC ok (fwtool), Netgear CHK board 'U12H139T00_NETGEAR'" && ok "TRX: CHK board and CRC" || bad "netgear.chk info: $($F info fw/netgear.chk 2>&1 | grep TRX)"
  $F info fw/bad.trx > /dev/null 2> fw/bad-trx.err; grep -q "CRC-32 of the TRX image does not match" fw/bad-trx.err && ok "TRX: a changed byte is reported by the CRC" || bad "bad.trx: $(cat fw/bad-trx.err)"
  echo added > fw/added.txt
  [ -e fw/big.bin ] || head -c 300000 /dev/urandom > fw/big.bin
  for f in v1.trx v2.trx std.trx netgear.chk; do
    cp fw/$f fw/w_$f
    if $F edit fw/w_$f fw/w_$f "add:/:fw/big.bin" > /dev/null 2> fw/w_trx.err && python3 -c "import sys; sys.path.insert(0, 'fw'); import trxlib; sys.exit(0 if trxlib.check('fw/w_$f') else 1)" \
       && python3 -c "import sys; d=open('fw/w_$f','rb').read(); a=d.index(b'hsqs'); open('fw/w_root.sqfs','wb').write(d[a:])" && rm -rf fw/wu && unsquashfs -n -d fw/wu fw/w_root.sqfs > /dev/null 2>&1 \
       && cmp -s fw/wu/big.bin fw/big.bin; then ok "TRX $f rebuilt: CRC and CHK checksums right, unsquashfs sees the change"
    else bad "TRX $f rebuilt: $(grep -v '^warning' fw/w_trx.err | head -2)"; fi
  done
  if [ -n "${TRX:-}" ] && [ -x "$TRX" ]; then
    head -c 64 /dev/urandom > fw/binhdr.bin
    local name args
    for name in v1 F v2; do
      case $name in
        v1) args=(-f fw/loader.bin -a 1024 -f fw/kernel.lzma -a 0x10000 -f);;
        F)  args=(-f fw/loader.bin -a 1024 -f fw/kernel.lzma -a 0x10000 -F);;
        v2) args=(-2 -f fw/loader.bin -a 1024 -f fw/kernel.lzma -a 0x10000 -f);;
      esac
      local tail=(); [ $name = v2 ] && tail=(-a 4 -f fw/binhdr.bin)
      "$TRX" -o fw/t_$name.trx "${args[@]}" img/xz.sqfs "${tail[@]}" 2>/dev/null
      if $F edit fw/t_$name.trx fw/t_$name.trx "add:/:fw/big.bin" > /dev/null 2> fw/t.err; then
        python3 -c "import struct; d=open('fw/t_$name.trx','rb').read(); a=d.index(b'hsqs', 4096); u=struct.unpack('<Q', d[a+40:a+48])[0]; open('fw/t_root.sqfs','wb').write(d[a:a+((u+4095)&~4095)])"
        "$TRX" -o fw/t_ref.trx "${args[@]}" fw/t_root.sqfs "${tail[@]}" 2>/dev/null
        cmp -s fw/t_$name.trx fw/t_ref.trx && ok "trx ($name): the rebuilt image is byte for byte what trx makes" || bad "trx ($name): the rebuilt image differs from trx"
      else bad "edit trx ($name): $(grep -v '^warning' fw/t.err | head -2)"; fi
    done
  else echo "  --    trx checks skipped: set TRX to the trx of OpenWrt's firmware-utils"; fi
  if [ -n "${MKCHKIMG:-}" ] && [ -x "$MKCHKIMG" ]; then
    local hl
    for name in default R7000; do
      args=(); [ $name = R7000 ] && args=(-b R7000 -r 2)
      "$MKCHKIMG" -o fw/c_$name.chk -k fw/v1.trx "${args[@]}" 2>/dev/null
      if $F dump fw/c_$name.chk 2>/dev/null | cmp -s - tree.ref && $F edit fw/c_$name.chk fw/c_$name.chk "add:/:fw/big.bin" > /dev/null 2> fw/c.err; then
        hl=$(python3 -c "import struct; print(struct.unpack('>I', open('fw/c_$name.chk','rb').read()[4:8])[0])")
        tail -c +$((hl + 1)) fw/c_$name.chk > fw/c_new.trx
        "$MKCHKIMG" -o fw/c_ref.chk -k fw/c_new.trx "${args[@]}" 2>/dev/null
        cmp -s fw/c_$name.chk fw/c_ref.chk && ok "mkchkimg ($name): read; the rebuilt CHK header is byte for byte what mkchkimg makes" || bad "mkchkimg ($name): the rebuilt CHK header differs"
      else bad "mkchkimg ($name): $(grep -v '^warning' fw/c.err 2>/dev/null | head -2)"; fi
    done
    # Without a TRX: -k kernel -f rootfs, and an OpenWrt sysupgrade image (uImage, SquashFS, padjffs2 markers) as -k.
    python3 - fw <<'PY2'
import sys, struct, zlib, os
d = sys.argv[1]; data = b'\x5d\x00\x00\x80\x00' + os.urandom(200000)
h = bytearray(struct.pack('>7I4B32s', 0x27051956, 0, 0, len(data), 0x80000000, 0x80000000, zlib.crc32(data), 5, 5, 2, 3, b'MIPS OpenWrt Linux'))
struct.pack_into('>I', h, 4, zlib.crc32(h)); open(d + '/c_uimage.bin', 'wb').write(bytes(h) + data)
PY2
    cat > fw/c_sysup.py <<'PY2'
import sys
k = open(sys.argv[1], 'rb').read(); r = open(sys.argv[2], 'rb').read()
b = bytearray(k + r)
# OpenWrt's padjffs2 with its default sizes (4 8 16 64 128 256 KiB): a marker at the next boundary of the smallest size
# left, then every size that boundary is aligned to is done.
sizes = {x << 10 for x in (4, 8, 16, 64, 128, 256)}
while sizes:
    m = min(sizes); n = (len(b) + m - 1) // m * m
    b += b'\xff' * (n - len(b)) + b'\xde\xad\xc0\xde'
    sizes = {x for x in sizes if n % x}
open(sys.argv[3], 'wb').write(b)
PY2
    for name in kf sysup; do
      for root in img/xz.sqfs fw/c_new.sqfs; do
        local out=fw/c_$name.chk; [ $root = fw/c_new.sqfs ] && out=fw/c_ref.chk
        case $name in
          kf) "$MKCHKIMG" -o $out -k fw/c_uimage.bin -f $root -b R7800 -r 2;;
          sysup) python3 fw/c_sysup.py fw/c_uimage.bin $root fw/c_su.bin; "$MKCHKIMG" -o $out -k fw/c_su.bin -b R6220;;
        esac > /dev/null 2>&1
        [ $root = fw/c_new.sqfs ] && break
        if $F dump fw/c_$name.chk 2>/dev/null | cmp -s - tree.ref && $F info fw/c_$name.chk 2>/dev/null | grep -q "Netgear CHK image for .*checksums ok\|checksums ok"; then ok "mkchkimg ($name): read as a CHK image, checksums right"; else bad "mkchkimg ($name): $($F info fw/c_$name.chk 2>&1 | grep -iE 'firmware|warning' | head -2)"; fi
        $F edit fw/c_$name.chk fw/c_$name.chk "add:/:fw/big.bin" > /dev/null 2> fw/c.err
        python3 -c "import struct; d=open('fw/c_$name.chk','rb').read(); a=d.index(b'hsqs', 64); u=struct.unpack('<Q', d[a+40:a+48])[0]; open('fw/c_new.sqfs','wb').write(d[a:a+((u+4095)&~4095)])"
      done
      cmp -s fw/c_$name.chk fw/c_ref.chk && ok "mkchkimg ($name): the rebuilt image is byte for byte what mkchkimg makes" || bad "mkchkimg ($name): the rebuilt image differs: $(grep -v '^warning' fw/c.err | head -2)"
    done
  else echo "  --    mkchkimg checks skipped: set MKCHKIMG to the mkchkimg of OpenWrt's firmware-utils"; fi
}

fw_tplink() {
  echo "TP-Link"
  python3 - fw img/xz.sqfs <<'PY'
# After OpenWrt's mktplinkfw.c (build_fw, fill_header) and tplink-safeloader.c (generate_factory_image, put_partitions).
import sys, struct, hashlib, os
d, root = sys.argv[1], open(sys.argv[2], 'rb').read()
SN = bytes.fromhex('dcd73aa5c39598fbddf9e7f40eae4738'); SS = bytes.fromhex('7a2b15ed9b98596de504ab44ac2a9f4e')
kernel = b'\x27\x05\x19\x56' + os.urandom(300000)
def classic(rootfs, strip=True, layout=0x7c0000, rootfs_ofs=0x100000, jffs2=False, combined=False):
    buf = bytearray(b'\xff' * layout); buf[:0x200] = bytes(0x200)
    k = kernel + (root if combined else b''); buf[0x200:0x200 + len(k)] = k; n = 0x200 + len(k)
    if not combined:
        buf[rootfs_ofs:rootfs_ofs + len(rootfs)] = rootfs; n = rootfs_ofs + len(rootfs)
        if jffs2:
            n = (n + 0xffff) & ~0xffff; buf[n:n + 4] = b'\xde\xad\xc0\xde'; n += 4
    img = buf[:n] if strip else buf
    struct.pack_into('>I', img, 0, 0x01000000); img[4:24] = b'TP-LINK Technologies'; img[28:36] = b'ver. 1.0'
    struct.pack_into('>II', img, 64, 0x07500001, 1)
    struct.pack_into('>9I', img, 116, 0x80060000, 0x80060000, layout, 0x200, len(k), 0 if combined else rootfs_ofs, 0 if combined else len(rootfs), 0, 0)
    img[76:92] = SN; img[76:92] = hashlib.md5(img).digest(); return bytes(img)
def meta(b): return struct.pack('>II', len(b), 0) + b
def safeloader(rootfs):
    flash = (b'\0\x04\0\0partition fs-uboot base 0x00000 size 0x20000\npartition firmware base 0x20000 size 0x7a0000\n\0').ljust(0x800, b'\xff')
    parts = [(b'partition-table', flash), (b'soft-version', meta(b'soft_ver:7.0.0\n\0')),
             (b'support-list', meta(b'SupportList:\r\n{product_name:Archer C7,product_ver:5.0.0,special_id:00000000}\r\n')),
             (b'os-image', kernel), (b'file-system', rootfs), (b'extra-para', meta(b'\x01\x00'))]
    table = b''; data = b''; base = 0x800
    for name, blob in parts:
        table += b'fwup-ptn %s base 0x%05x size 0x%05x\t\r\n' % (name, base, len(blob)); data += blob; base += len(blob)
    vendor = b'vendor_name:TP-Link\nvendor_id:0\n'
    img = bytearray(0x14) + (struct.pack('>I', len(vendor)) + vendor).ljust(0x1000, b'\xff') + (table + b'\0').ljust(0x800, b'\xff') + data
    struct.pack_into('>I', img, 0, len(img)); img[4:20] = hashlib.md5(SS + bytes(img[0x14:])).digest(); return bytes(img)
open(d + '/tp-strip.bin', 'wb').write(classic(root))
open(d + '/tp-full.bin', 'wb').write(classic(root, strip=False))
open(d + '/tp-jffs2.bin', 'wb').write(classic(root, jffs2=True))
open(d + '/tp-combined.bin', 'wb').write(classic(root, combined=True))
open(d + '/tp-factory.bin', 'wb').write(safeloader(root))
b = bytearray(open(d + '/tp-strip.bin', 'rb').read()); b[-200] ^= 1; open(d + '/tp-bad.bin', 'wb').write(b)
open(d + '/tp-kernel.bin', 'wb').write(kernel)
PY
  local f
  for f in tp-strip tp-full tp-jffs2 tp-combined tp-factory; do
    cmpdump "TP-Link: $f.bin" fw/$f.bin tree.ref
    $F info fw/$f.bin 2>/dev/null | grep -q "MD5 ok" && ok "TP-Link: $f.bin MD5 found right" || bad "TP-Link $f.bin MD5: $($F info fw/$f.bin 2>&1 | grep -i md5)"
  done
  $F info fw/tp-bad.bin > /dev/null 2> fw/tp-bad.err; grep -q "MD5 sum in the TP-Link header does not match" fw/tp-bad.err && ok "TP-Link: a changed byte is reported by the MD5" || bad "tp-bad.bin: $(cat fw/tp-bad.err)"
  if [ -n "${MKTPLINKFW:-}" ] && [ -x "$MKTPLINKFW" ]; then
    "$MKTPLINKFW" -H 0x07500001 -W 1 -F 8Mlzma -N "TP-LINK Technologies" -V "ver. 1.0" -k fw/tp-kernel.bin -r img/xz.sqfs -o fw/m-strip.bin -s > /dev/null 2>&1
    "$MKTPLINKFW" -H 0x07500001 -W 1 -F 4Mlzma -k fw/tp-kernel.bin -r img/xz.sqfs -o fw/m-full.bin > /dev/null 2>&1
    "$MKTPLINKFW" -H 0x07500001 -W 1 -F 8Mlzma -k fw/tp-kernel.bin -r img/xz.sqfs -o fw/m-jffs2.bin -s -j > /dev/null 2>&1
    cat fw/tp-kernel.bin img/xz.sqfs > fw/m-k+r.bin; "$MKTPLINKFW" -H 0x07500001 -W 1 -F 8Mlzma -c -k fw/m-k+r.bin -o fw/m-combined.bin -s > /dev/null 2>&1
    "$MKTPLINKFW" -H 0x07500001 -W 1 -F 8Mlzma -k fw/tp-kernel.bin -r img/xz.sqfs -o fw/m-align.bin -s -a 0x10000 > /dev/null 2>&1
    for f in m-strip m-full m-jffs2 m-combined m-align; do
      if $F dump fw/$f.bin 2>/dev/null | cmp -s - tree.ref && $F info fw/$f.bin 2>/dev/null | grep -q "MD5 ok"; then ok "mktplinkfw ($f): read, MD5 right"; else bad "mktplinkfw ($f)"; fi
    done
  else echo "  --    mktplinkfw checks skipped: set MKTPLINKFW to the mktplinkfw of OpenWrt's firmware-utils"; fi
  if [ -n "${TPLINK_SAFELOADER:-}" ] && [ -x "$TPLINK_SAFELOADER" ]; then
    "$TPLINK_SAFELOADER" -B ARCHER-C7-V5 -k fw/tp-kernel.bin -r img/xz.sqfs -o fw/s-factory.bin > /dev/null 2>&1
    "$TPLINK_SAFELOADER" -B ARCHER-C7-V5 -k fw/tp-kernel.bin -r img/xz.sqfs -o fw/s-factory-j.bin -j > /dev/null 2>&1
    for f in s-factory s-factory-j; do
      if $F dump fw/$f.bin 2>/dev/null | cmp -s - tree.ref && $F info fw/$f.bin 2>/dev/null | grep -q "safeloader, MD5 ok"; then ok "tplink-safeloader ($f): read, MD5 right"; else bad "tplink-safeloader ($f)"; fi
    done
  else echo "  --    tplink-safeloader checks skipped: set TPLINK_SAFELOADER to the tplink-safeloader of OpenWrt's firmware-utils"; fi
  [ -e fw/big.bin ] || head -c 300000 /dev/urandom > fw/big.bin
  cat > fw/tpcheck.py <<'PY'
# The MD5 of a TP-Link image as mktplinkfw and tplink-safeloader compute it.
import sys, struct, hashlib
d = bytearray(open(sys.argv[1], 'rb').read())
if d[:4] == b'\x01\x00\x00\x00':
    s = bytes(d[76:92]); d[76:92] = bytes.fromhex('dcd73aa5c39598fbddf9e7f40eae4738'); ok = hashlib.md5(d).digest() == s
else:
    n = struct.unpack('>I', d[:4])[0]; ok = n == len(d) and hashlib.md5(bytes.fromhex('7a2b15ed9b98596de504ab44ac2a9f4e') + bytes(d[0x14:n])).digest() == bytes(d[4:20])
sys.exit(0 if ok else 1)
PY
  for f in tp-strip tp-full tp-jffs2 tp-combined tp-factory; do
    cp fw/$f.bin fw/w_$f.bin
    if $F edit fw/w_$f.bin fw/w_$f.bin "add:/:fw/big.bin" > /dev/null 2> fw/w_tp.err && python3 fw/tpcheck.py fw/w_$f.bin \
       && python3 -c "import sys,struct; d=open('fw/w_$f.bin','rb').read(); a=d.index(b'hsqs', 0x200); u=struct.unpack('<Q', d[a+40:a+48])[0]; open('fw/w_tp.sqfs','wb').write(d[a:a+((u+4095)&~4095)])" \
       && rm -rf fw/wtu && unsquashfs -n -d fw/wtu fw/w_tp.sqfs > /dev/null 2>&1 && cmp -s fw/wtu/big.bin fw/big.bin; then
      ok "TP-Link $f.bin rebuilt: MD5 right, unsquashfs sees the change"
    else bad "TP-Link $f.bin rebuilt: $(grep -v '^warning' fw/w_tp.err | head -2)"; fi
  done
  local args
  if [ -n "${MKTPLINKFW:-}" ] && [ -x "$MKTPLINKFW" ]; then
    for f in m-strip m-full m-jffs2 m-combined m-align; do
      cp fw/$f.bin fw/w_$f.bin
      $F edit fw/w_$f.bin fw/w_$f.bin "add:/:fw/big.bin" > /dev/null 2>&1
      python3 -c "import sys,struct; d=open('fw/w_$f.bin','rb').read(); a=d.index(b'hsqs', 0x200); u=struct.unpack('<Q', d[a+40:a+48])[0]; open('fw/w_tp.sqfs','wb').write(d[a:a+((u+4095)&~4095)])"
      case $f in
        m-strip) "$MKTPLINKFW" -H 0x07500001 -W 1 -F 8Mlzma -N "TP-LINK Technologies" -V "ver. 1.0" -k fw/tp-kernel.bin -r fw/w_tp.sqfs -o fw/ref.bin -s > /dev/null 2>&1;;
        m-full) "$MKTPLINKFW" -H 0x07500001 -W 1 -F 4Mlzma -k fw/tp-kernel.bin -r fw/w_tp.sqfs -o fw/ref.bin > /dev/null 2>&1;;
        m-jffs2) "$MKTPLINKFW" -H 0x07500001 -W 1 -F 8Mlzma -k fw/tp-kernel.bin -r fw/w_tp.sqfs -o fw/ref.bin -s -j > /dev/null 2>&1;;
        m-combined) cat fw/tp-kernel.bin fw/w_tp.sqfs > fw/m-k+r2.bin; "$MKTPLINKFW" -H 0x07500001 -W 1 -F 8Mlzma -c -k fw/m-k+r2.bin -o fw/ref.bin -s > /dev/null 2>&1;;
        m-align) "$MKTPLINKFW" -H 0x07500001 -W 1 -F 8Mlzma -k fw/tp-kernel.bin -r fw/w_tp.sqfs -o fw/ref.bin -s -a 0x10000 > /dev/null 2>&1;;
      esac
      cmp -s fw/w_$f.bin fw/ref.bin && ok "mktplinkfw ($f): the rebuilt image is byte for byte what mktplinkfw makes" || bad "mktplinkfw ($f): the rebuilt image differs"
    done
  fi
  if [ -n "${TPLINK_SAFELOADER:-}" ] && [ -x "$TPLINK_SAFELOADER" ]; then
    for f in s-factory s-factory-j; do
      cp fw/$f.bin fw/w_$f.bin
      $F edit fw/w_$f.bin fw/w_$f.bin "add:/:fw/big.bin" > /dev/null 2>&1
      python3 -c "import sys,struct; d=open('fw/w_$f.bin','rb').read(); a=d.index(b'hsqs', 0x200); u=struct.unpack('<Q', d[a+40:a+48])[0]; open('fw/w_tp.sqfs','wb').write(d[a:a+((u+4095)&~4095)])"
      args=(); [ $f = s-factory-j ] && args=(-j)
      "$TPLINK_SAFELOADER" -B ARCHER-C7-V5 -k fw/tp-kernel.bin -r fw/w_tp.sqfs -o fw/ref.bin "${args[@]}" > /dev/null 2>&1
      cmp -s fw/w_$f.bin fw/ref.bin && ok "tplink-safeloader ($f): the rebuilt image is byte for byte what tplink-safeloader makes" || bad "tplink-safeloader ($f): the rebuilt image differs"
    done
  fi
  if [ -n "${MKTPLINKFW2:-}" ] && [ -x "$MKTPLINKFW2" ]; then
    # Header versions 2 and 3: each image read, changed, and compared with what mktplinkfw2 makes from the new filesystem.
    local root
    for f in v2-strip v2-full v3-jffs2 v2-align v2-combined; do
      for root in img/xz.sqfs fw/w_tp.sqfs; do
        local out=fw/$f.bin; [ $root = fw/w_tp.sqfs ] && out=fw/ref.bin
        case $f in
          v2-strip) "$MKTPLINKFW2" -B TD-W8970v1 -k fw/tp-kernel.bin -r $root -o $out -s;;
          v2-full) "$MKTPLINKFW2" -B TD-W8980v1 -k fw/tp-kernel.bin -r $root -o $out;;
          v3-jffs2) "$MKTPLINKFW2" -B ArcherC20i -k fw/tp-kernel.bin -r $root -o $out -s -j;;
          v2-align) "$MKTPLINKFW2" -B ArcherVR200V -k fw/tp-kernel.bin -r $root -o $out -s -a 0x10000;;
          v2-combined) cat fw/tp-kernel.bin $root > fw/m2-k+r.bin; "$MKTPLINKFW2" -B TD-W8970v1 -c -k fw/m2-k+r.bin -o $out -s;;
        esac > /dev/null 2>&1
        [ $root = fw/w_tp.sqfs ] && break
        if $F dump fw/$f.bin 2>/dev/null | cmp -s - tree.ref && $F info fw/$f.bin 2>/dev/null | grep -q "MD5 ok"; then ok "mktplinkfw2 ($f): read, MD5 right"; else bad "mktplinkfw2 ($f)"; fi
        cp fw/$f.bin fw/w_$f.bin
        $F edit fw/w_$f.bin fw/w_$f.bin "add:/:fw/big.bin" > /dev/null 2>&1
        python3 -c "import sys,struct; d=open('fw/w_$f.bin','rb').read(); a=d.index(b'hsqs', 0x200); u=struct.unpack('<Q', d[a+40:a+48])[0]; open('fw/w_tp.sqfs','wb').write(d[a:a+((u+4095)&~4095)])"
      done
      cmp -s fw/w_$f.bin fw/ref.bin && ok "mktplinkfw2 ($f): the rebuilt image is byte for byte what mktplinkfw2 makes" || bad "mktplinkfw2 ($f): the rebuilt image differs"
    done
  else echo "  --    mktplinkfw2 checks skipped: set MKTPLINKFW2 to the mktplinkfw2 of OpenWrt's firmware-utils"; fi
}

