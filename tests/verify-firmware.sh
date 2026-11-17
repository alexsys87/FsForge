# Vendor firmware checks. Sourced by verify-linux.sh, which provides $F (the CLI), tree/ with its listing tree.ref,
# img/xz.sqfs (the same tree), ok/bad and cmpdump; everything else lives in fw/.
#
#   fw_run      U-Boot uImage files made by mkimage (u-boot-tools) are opened at the filesystem they hold and must
#               read as the image itself: an OpenWrt-style sysupgrade image (an lzma kernel uImage padded to the
#               erase block, the SquashFS behind it, the JFFS2 marker), a JFFS2 root behind the kernel, a filesystem
#               uImage, a uInitrd with a GNU cpio archive in gzip, a gzipped ext2 initrd, and a multi-file image
#               (kernel, ramdisk, device tree) whose parts are chosen with vol=. The firmware is not rebuilt yet:
#               saving over it is refused and leaves the file as it was; saving under another name writes the
#               filesystem alone.

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

  local sum; sum=$(sha256sum < fw/sysupgrade.bin)
  if $F edit fw/sysupgrade.bin fw/sysupgrade.bin rm:/etc 2> fw/same.err; then bad "the firmware was saved over"
  else [ "$(sha256sum < fw/sysupgrade.bin)" = "$sum" ] && grep -q "Save under another name" fw/same.err && ok "uImage: saving over the firmware is refused, the file is unchanged" \
    || bad "save over the firmware: $(cat fw/same.err)"; fi
  if $F convert fw/sysupgrade.bin fw/rootfs.sqfs > /dev/null 2> fw/alone.err && command -v unsquashfs >/dev/null && unsquashfs -n -d fw/un fw/rootfs.sqfs > /dev/null 2>&1; then
    python3 scan.py fw/un | cmp -s - tree.ref && ok "uImage: the filesystem saved alone, unpacked by unsquashfs" || bad "the filesystem saved alone differs"
  else bad "save the filesystem alone: $(cat fw/alone.err)"; fi
}
