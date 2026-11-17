# Android container checks. Sourced by verify-linux.sh, which provides $F (the CLI), tree/ with its listing
# tree.ref, ok/bad; everything else lives in android/.
#
#   android_run   super.img: images made by lpmake (raw and sparse, two slots, a group with a maximum size, an empty
#                 partition) from an ext4 image of mke2fs and an EROFS image of mkfs.erofs are opened at each
#                 partition and read as the images themselves; a partition is changed in place and another one
#                 grown into the free space, and the results are unpacked by lpunpack (which checks the metadata)
#                 and compared: the changed partition reads as FsForge saved it, the other one is byte for byte
#                 the same. A sparse super image stays sparse (simg2img unpacks it).
#                 boot images: mkbootimg makes boot images of header versions 0 to 4 (gzip and lz4 ramdisks) and
#                 vendor_boot images of versions 3 and 4 (two ramdisk fragments); FsForge adds a file to the
#                 ramdisk and saves, and mkbootimg, given the same pieces and the ramdisk unpack_bootimg takes out
#                 of the result, must make the very same file (header, sizes, SHA-1 id, ramdisk table).
#
# lpmake and lpunpack (system/extras/partition_tools) are not packaged by distributions: build them from AOSP
# and give their folder in ANDROID_LP; without them the super.img part is skipped. mkbootimg and unpack_bootimg
# come with the "mkbootimg" package (some builds of it import a "gki" module the package lacks; the boot image
# part is then skipped).

android_run() {
  rm -rf android; mkdir -p android/img android/out android/u
  local A=android/img
  echo "Android: super.img made by lpmake"
  local lp=${ANDROID_LP:-}
  if [ -n "$lp" ] && [ -x "$lp/lpmake" ] && [ -x "$lp/lpunpack" ] && command -v mke2fs >/dev/null && command -v mkfs.erofs >/dev/null; then
    mke2fs -q -t ext4 -b 4096 -d "$PWD/tree" $A/system.img 64M
    rm -rf android/small; mkdir -p android/small/etc; echo vendor > android/small/etc/v.txt; cp /bin/ls android/small/
    mkfs.erofs -zlz4hc $A/vendor.img android/small >/dev/null 2>&1
    local ss vs; ss=$(stat -c %s $A/system.img); vs=$(stat -c %s $A/vendor.img)
    local common=(--metadata-size 65536 --super-name super --metadata-slots 2 --device super:268435456 --group main:200000000
      --partition system_a:readonly:$ss:main --image system_a=$A/system.img --partition vendor_a:readonly:$vs:main --image vendor_a=$A/vendor.img
      --partition system_b:readonly:0:main)
    "$lp/lpmake" "${common[@]}" --output $A/super.img > android/out/lpmake.log 2>&1
    "$lp/lpmake" "${common[@]}" --sparse --output $A/super_sparse.img >> android/out/lpmake.log 2>&1
    $F dump $A/system.img > android/out/system.ref 2>/dev/null; $F dump $A/vendor.img > android/out/vendor.ref 2>/dev/null
    local img v
    for img in super super_sparse; do
      for v in system_a vendor_a; do
        $F dump $A/$img.img vol=$v > android/out/$img.$v.txt 2> android/out/$img.$v.err && cmp -s android/out/$img.$v.txt android/out/${v%_a}.ref \
          && ok "read  $img.img: partition $v" || bad "read $img.img vol=$v: $(head -c 200 android/out/$img.$v.err)"
      done
    done
    $F info $A/super.img vol=system_b > /dev/null 2> android/out/empty.err; grep -q "cannot be opened" android/out/empty.err \
      && ok "an empty partition is refused" || bad "empty partition: $(cat android/out/empty.err)"

    lpcheck() { # name image changed-partition unchanged-partition source-of-unchanged : lpunpack, then compare
      local n=$1 img=$2 ch=$3 un=$4 src=$5 raw=$2
      case $(head -c 4 $img | od -An -tx1 | tr -d ' ') in 3aff26ed) raw=android/out/$n.raw; simg2img $img $raw;; esac
      rm -rf android/u/$n; mkdir -p android/u/$n
      if ! "$lp/lpunpack" $raw android/u/$n > android/out/lpunpack.$n.log 2>&1; then bad "lpunpack rejects $n: $(tail -1 android/out/lpunpack.$n.log)"; return; fi
      $F dump android/u/$n/$ch.img > android/out/$n.unpacked 2>/dev/null; $F dump $img vol=$ch > android/out/$n.fsforge 2>/dev/null
      if cmp -s android/out/$n.unpacked android/out/$n.fsforge && cmp -s android/u/$n/$un.img $src; then ok "lpunpack takes $n apart: $ch as saved, $un unchanged"
      else bad "$n: lpunpack output differs"; fi
    }
    $F edit $A/super.img $A/w_inplace.img rm:/one-byte verify=content > /dev/null 2> android/out/w1.err && lpcheck w_inplace $A/w_inplace.img system_a vendor_a $A/vendor.img \
      || bad "edit super.img: $(cat android/out/w1.err)"
    $F edit $A/super.img $A/w_grown.img vol=vendor_a "add:/:tree/usr" verify=content > /dev/null 2> android/out/w2.err && lpcheck w_grown $A/w_grown.img vendor_a system_a $A/system.img \
      || bad "grow vendor_a: $(cat android/out/w2.err)"
    $F edit $A/super_sparse.img $A/w_sparse.img "add:/:android/small/etc" verify=content > /dev/null 2> android/out/w3.err \
      && [ "$(head -c 4 $A/w_sparse.img | od -An -tx1 | tr -d ' ')" = 3aff26ed ] && lpcheck w_sparse $A/w_sparse.img system_a vendor_a $A/vendor.img \
      || bad "edit sparse super: $(cat android/out/w3.err)"
  else echo "  --    lpmake/lpunpack (ANDROID_LP), mke2fs or mkfs.erofs missing: super.img skipped"; fi

  echo "Android: boot images made by mkbootimg"
  if ! command -v mkbootimg >/dev/null || ! command -v unpack_bootimg >/dev/null; then echo "  --    mkbootimg is not installed: skipped"; return; fi
  if mkbootimg --help 2>&1 | grep -q "No module named"; then echo "  --    mkbootimg does not start (a Python module of it is missing): skipped"; return; fi
  local B=android/boot; mkdir -p $B
  rm -rf android/small2; mkdir -p android/small2/sbin; echo init > android/small2/init.rc; ln -s ../init.rc android/small2/sbin/link
  (cd android/small2 && find . | cpio -o -H newc --quiet) > $B/rd.cpio; gzip -9c $B/rd.cpio > $B/rd.gz
  command -v lz4 >/dev/null && lz4 -l -12 -f -q $B/rd.cpio $B/rd.lz4 || cp $B/rd.gz $B/rd.lz4
  head -c 300000 /dev/urandom > $B/kernel; head -c 5000 /dev/urandom > $B/second; head -c 7000 /dev/urandom > $B/dtb; head -c 3000 /dev/urandom > $B/dtbo
  echo hello > $B/extra.txt
  bk() { # name mkbootimg arguments with @RD@ for the ramdisk
    local n=$1; shift
    local args=() a; for a in "$@"; do args+=("${a//@RD@/$B/rd.$( [ "$n" = v0 ] || [ "$n" = v1 ] && echo gz || echo lz4)}"); done
    mkbootimg "${args[@]}" --output $B/$n.img > /dev/null 2>&1 || { bad "mkbootimg $n"; return; }
    if ! $F edit $B/$n.img $B/w_$n.img "add:/:$B/extra.txt" verify=content > /dev/null 2> android/out/b.$n.err; then bad "edit $n: $(cat android/out/b.$n.err)"; return; fi
    rm -rf android/u/b; unpack_bootimg --boot_img $B/w_$n.img --out android/u/b > /dev/null 2>&1
    args=(); for a in "$@"; do args+=("${a//@RD@/android/u/b/ramdisk}"); done
    mkbootimg "${args[@]}" --output $B/ref_$n.img > /dev/null 2>&1
    cmp -s $B/w_$n.img $B/ref_$n.img && $F dump $B/w_$n.img 2>/dev/null | grep -q "^/extra.txt" \
      && ok "boot $n: FsForge's image is what mkbootimg makes from the new ramdisk" || bad "boot $n differs from mkbootimg"
  }
  bk v0 --header_version 0 --kernel $B/kernel --ramdisk @RD@ --second $B/second --cmdline "console=ttyS0" --base 0x10000000 --pagesize 2048 --id
  bk v1 --header_version 1 --kernel $B/kernel --ramdisk @RD@ --recovery_dtbo $B/dtbo --cmdline "a=b" --pagesize 4096 --id
  bk v2 --header_version 2 --kernel $B/kernel --ramdisk @RD@ --second $B/second --recovery_dtbo $B/dtbo --dtb $B/dtb --pagesize 2048 --id
  bk v3 --header_version 3 --kernel $B/kernel --ramdisk @RD@ --cmdline "x=1"
  bk v4 --header_version 4 --ramdisk @RD@

  mkbootimg --header_version 3 --vendor_boot $B/vb3.img --vendor_ramdisk $B/rd.gz --dtb $B/dtb --vendor_cmdline "vc=1" --pagesize 4096 > /dev/null 2>&1
  if $F edit $B/vb3.img $B/w_vb3.img "add:/:$B/extra.txt" verify=content > /dev/null 2> android/out/vb3.err; then
    rm -rf android/u/v; unpack_bootimg --boot_img $B/w_vb3.img --out android/u/v > /dev/null 2>&1
    mkbootimg --header_version 3 --vendor_boot $B/ref_vb3.img --vendor_ramdisk android/u/v/vendor_ramdisk --dtb $B/dtb --vendor_cmdline "vc=1" --pagesize 4096 > /dev/null 2>&1
    cmp -s $B/w_vb3.img $B/ref_vb3.img && ok "vendor_boot v3: identical to mkbootimg" || bad "vendor_boot v3 differs from mkbootimg"
  else bad "edit vendor_boot v3: $(cat android/out/vb3.err)"; fi
  local frag=(--ramdisk_type platform --ramdisk_name plat --vendor_ramdisk_fragment @A@ --ramdisk_type dlkm --ramdisk_name dlkm --vendor_ramdisk_fragment @B@
    --dtb $B/dtb --vendor_bootconfig $B/dtbo --pagesize 4096)
  local f1=() a; for a in "${frag[@]}"; do a=${a//@A@/$B/rd.lz4}; f1+=("${a//@B@/$B/rd.gz}"); done
  mkbootimg --header_version 4 --vendor_boot $B/vb4.img "${f1[@]}" > /dev/null 2>&1
  if $F edit $B/vb4.img $B/w_vb4.img vol=plat "add:/:$B/extra.txt" verify=content > /dev/null 2> android/out/vb4.err; then
    rm -rf android/u/v; unpack_bootimg --boot_img $B/w_vb4.img --out android/u/v > /dev/null 2>&1
    local f2=(); for a in "${frag[@]}"; do a=${a//@A@/android/u/v/vendor_ramdisk00}; f2+=("${a//@B@/android/u/v/vendor_ramdisk01}"); done
    mkbootimg --header_version 4 --vendor_boot $B/ref_vb4.img "${f2[@]}" > /dev/null 2>&1
    cmp -s $B/w_vb4.img $B/ref_vb4.img && cmp -s android/u/v/vendor_ramdisk01 $B/rd.gz \
      && ok "vendor_boot v4: identical to mkbootimg, the other fragment unchanged" || bad "vendor_boot v4 differs from mkbootimg"
  else bad "edit vendor_boot v4: $(cat android/out/vb4.err)"; fi
}
