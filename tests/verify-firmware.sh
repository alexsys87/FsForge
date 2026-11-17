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
