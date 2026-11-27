using System.Text;
using FsForge.Core;
using FsForge.Core.Images;
using FsForge.Core.SquashFs;
using FsForge.Core.Tar;
using FsForge.Presentation.Services;
using FsForge.Presentation.Settings;
using FsForge.Presentation.ViewModels;

namespace FsForge.Presentation.Tests;

/// <summary>Scripted stand-in for the user: answers questions and fills in dialogs.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public readonly Queue<string?> OpenPicks = new(), FolderPicks = new(), SavePicks = new();
    public readonly Queue<IReadOnlyList<string>> FilePicks = new();
    public readonly Queue<DialogAnswer> Answers = new();
    public readonly List<string> Asked = new(), Messages = new(), Notifications = new();
    public Func<object, bool> OnDialog = _ => false;

    public string? PickImageToOpen(string? initialDirectory) => OpenPicks.Dequeue();
    public IReadOnlyList<string> PickFilesToAdd(string? initialDirectory) => FilePicks.Dequeue();
    public string? PickFolder(string title, string? initialDirectory) => FolderPicks.Dequeue();
    public string? PickSaveFile(string title, string suggestedName, ImageFormat format, string extension, string? initialDirectory, bool diskImage = false)
    {
        LastSuggestedName = suggestedName;
        LastSaveWasDisk = diskImage;
        return SavePicks.Dequeue();
    }

    public string LastSuggestedName = "";
    public bool LastSaveWasDisk;
    public bool ShowDialog(object viewModel) => OnDialog(viewModel);

    public Task<DialogAnswer> AskAsync(string title, string message, string primary, string? secondary = null, string? cancel = null)
    {
        Asked.Add(title);
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : throw new InvalidOperationException("Unexpected question: " + title + " / " + message));
    }

    public Task ShowMessageAsync(string title, string message, MessageKind kind = MessageKind.Info)
    {
        Messages.Add($"{kind}: {title}: {message}");
        return Task.CompletedTask;
    }

    public void Notify(string title, string message, MessageKind kind = MessageKind.Info) => Notifications.Add($"{kind}: {title}: {message}");
}

internal sealed class FakeShell : IShellService
{
    public readonly List<string> Folders = new(), Files = new();
    public string Clipboard = "";
    public void OpenFolder(string path) => Folders.Add(path);
    public void OpenFile(string path, string? program) => Files.Add(path);
    public void SetClipboardText(string text) => Clipboard = text;
}

internal static class Program
{
    private static int _passed, _failed;
    private static string _dir = "";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task Test(string name, Func<Task> body)
    {
        try
        {
            await body();
            _passed++;
            Console.WriteLine("  ok    " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>A super image with one extent per partition at the given sector (liblp layout, 2 slots).</summary>
    private static byte[] BuildSuper(long device, (string Name, long Sector, byte[] Data)[] parts)
    {
        const uint metaMax = 65536, slots = 2;
        byte[] img = new byte[device];
        byte[] g = new byte[52];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(g, 0x616C4467);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(4), 52);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(40), metaMax);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(44), slots);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan(48), 4096);
        System.Security.Cryptography.SHA256.HashData(g).CopyTo(g, 8);
        g.CopyTo(img, 4096);
        g.CopyTo(img, 8192);
        var t = new List<byte>();
        int extents = 0;
        foreach ((string name, _, byte[] data) in parts)
        {
            byte[] e = new byte[52];
            Encoding.ASCII.GetBytes(name).CopyTo(e, 0);
            e[36] = 1;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(40), (uint)extents);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(44), data.Length > 0 ? 1u : 0u);
            t.AddRange(e);
            if (data.Length > 0) extents++;
        }
        int ptSize = t.Count;
        foreach ((_, long sector, byte[] data) in parts)
        {
            if (data.Length == 0) continue;
            byte[] x = new byte[24];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(x, (ulong)data.Length / 512);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(x.AsSpan(12), (ulong)sector);
            t.AddRange(x);
            data.CopyTo(img, sector * 512);
        }
        byte[] grp = new byte[48];
        Encoding.ASCII.GetBytes("default").CopyTo(grp, 0);
        t.AddRange(grp);
        byte[] bd = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bd, (4096 + (4096 + metaMax * slots) * 2) / 512);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bd.AsSpan(8), 1 << 20);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bd.AsSpan(16), (ulong)device);
        Encoding.ASCII.GetBytes("super").CopyTo(bd, 24);
        t.AddRange(bd);
        byte[] tables = t.ToArray();
        byte[] h = new byte[128];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(h, 0x414C5030);
        h[4] = 10;
        h[8] = 128;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(44), (uint)tables.Length);
        System.Security.Cryptography.SHA256.HashData(tables).CopyTo(h, 48);
        int[] desc = { 0, parts.Length, 52, ptSize, extents, 24, ptSize + extents * 24, 1, 48, ptSize + extents * 24 + 48, 1, 64 };
        for (int i = 0; i < desc.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80 + i * 4), (uint)desc[i]);
        System.Security.Cryptography.SHA256.HashData(h).CopyTo(h, 12);
        for (int s = 0; s < 2 * slots; s++)
        {
            h.CopyTo(img, 12288 + s * metaMax);
            tables.CopyTo(img, 12288 + s * metaMax + 128);
        }
        return img;
    }

    /// <summary>A boot image with header version 3 (page 4096).</summary>
    private static byte[] BuildBootV3(byte[] kernel, byte[] ramdisk)
    {
        static int Pages(int n) => (n + 4095) / 4096 * 4096;
        byte[] img = new byte[4096 + Pages(kernel.Length) + Pages(ramdisk.Length)];
        Encoding.ASCII.GetBytes("ANDROID!").CopyTo(img, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(8), (uint)kernel.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(12), (uint)ramdisk.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(20), 1580);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(img.AsSpan(40), 3);
        kernel.CopyTo(img, 4096);
        ramdisk.CopyTo(img, 4096 + Pages(kernel.Length));
        return img;
    }

    private static string MakeImage(string name)
    {
        using var t = new FsTree(0x1ED, 1700000000);
        FsNode etc = t.CreateDirectory(t.Root, "etc", 0x1ED, 0, 0, 1700000001);
        FsNode bin = t.CreateDirectory(t.Root, "bin", 0x1ED, 0, 0, 1700000002);
        FsNode usr = t.CreateDirectory(t.Root, "usr", 0x1ED, 0, 0, 1700000003);
        FsNode lib = t.CreateDirectory(usr, "lib", 0x1ED, 0, 0, 1700000004);
        t.CreateFile(etc, "passwd", new MemoryFileData(Encoding.ASCII.GetBytes("root:x:0:0:root:/root:/bin/sh\nwww:x:33:33::/var/www:/bin/false\n")), 0x1A4, 0, 0, 1700000010);
        t.CreateFile(etc, "group", new MemoryFileData(Encoding.ASCII.GetBytes("root:x:0:\nwww-data:x:33:\n")), 0x1A4, 0, 0, 1700000010);
        t.CreateFile(etc, "hostname", new MemoryFileData(Encoding.ASCII.GetBytes("board\n")), 0x1A4, 33, 33, 1700000011);
        t.CreateFile(etc, "Banner", new MemoryFileData(Encoding.ASCII.GetBytes("hello\n")), 0x1A4, 0, 0, 1700000012);
        FsNode sh = t.CreateFile(bin, "busybox", new MemoryFileData(new byte[50000]), 0x1ED, 0, 0, 1700000020);
        t.CreateSymlink(bin, "sh", "busybox");
        t.CreateSymlink(t.Root, "lib", "usr/lib");
        t.CreateHardLink(bin, "ash", sh);
        for (int i = 0; i < 30; i++) t.CreateFile(lib, $"lib{i:00}.so", new MemoryFileData(new byte[1000 + i]), 0x1A4, 0, 0, 1700000100 + i);
        t.CreateSpecial(t.CreateDirectory(t.Root, "dev"), "null", FsNodeType.CharDevice, 1, 3, 0x1B6);
        string path = Path.Combine(_dir, name);
        using var fs = new FileStream(path, FileMode.Create);
        ImageDocument.WriteTo(t, fs, new SaveOptions { Format = ImageFormat.SquashFs, SquashFs = new SquashFsOptions { Compression = SquashCompression.Gzip } });
        return path;
    }

    /// <summary>An SD card image: MBR, an ext2 boot partition and the SquashFS of <see cref="MakeImage"/> as partition 2, gzipped.</summary>
    private static string MakeDisk(string name, out long rootOffset)
    {
        const int mib = 1 << 20;
        using var boot = new FsTree(0x1ED, 1700000000);
        boot.CreateFile(boot.Root, "uImage", new MemoryFileData(new byte[100000]), 0x1A4, 0, 0, 1700000001);
        var bootImage = new MemoryStream();
        ImageDocument.WriteTo(boot, bootImage, new SaveOptions { Format = ImageFormat.Ext, Ext = new Core.Ext.ExtOptions { Kind = Core.Ext.ExtKind.Ext2, SizeBytes = mib, BlockSize = 1024, Label = "boot" } });
        byte[] root = File.ReadAllBytes(MakeImage(name + ".root"));

        long p1 = mib, p2 = 2 * mib, slot2 = 4 * mib;
        byte[] disk = new byte[p2 + (root.Length + 0xFFFF) / 0x10000 * 0x10000];       // the file ends behind the padded filesystem
        void Entry(int slot, byte type, long offset, long length)
        {
            Span<byte> e = disk.AsSpan(446 + 16 * slot, 16);
            e[4] = type;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)(offset / 512));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)(length / 512));
        }
        Entry(0, 0x83, p1, mib);
        Entry(1, 0x83, p2, slot2);
        disk[510] = 0x55; disk[511] = 0xAA;
        new Random(3).NextBytes(disk.AsSpan(512, 4096));                                // a boot loader
        bootImage.ToArray().CopyTo(disk, p1);
        root.CopyTo(disk, p2);
        rootOffset = p2;

        string path = Path.Combine(_dir, name);
        using var fs = new FileStream(path, FileMode.Create);
        using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest);
        gz.Write(disk);
        return path;
    }

    private static byte[] Gunzip(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
        var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    private static (MainViewModel Vm, FakeDialogs Dialogs, FakeShell Shell, SettingsStore Store) NewVm()
    {
        var dialogs = new FakeDialogs();
        var shell = new FakeShell();
        var store = new SettingsStore(Path.Combine(_dir, "settings-" + Guid.NewGuid().ToString("N")[..6] + ".json"));
        var settings = new AppSettings { TempDirectory = _dir, OpenFolderAfterExtract = true, KeepBackup = false, Verify = VerifyLevel.Content };
        var vm = new MainViewModel(dialogs, shell, new ImmediateDispatcher(), store, settings);
        // The view normally applies requested selections; do the same here.
        vm.SelectionRequested += items => vm.SetSelection(items);
        return (vm, dialogs, shell, store);
    }

    private static byte[] BigEndian(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>A FIT image as mkimage makes it: a device tree (version 17) with the images in /images, each with a sha256 hash.</summary>
    private static byte[] BuildFit(params (string Name, string Type, byte[] Data)[] images)
    {
        var st = new MemoryStream();
        var strings = new MemoryStream();
        var names = new Dictionary<string, int>();
        void Word(uint v) => st.Write(BigEndian(v));
        void Pad() { while (st.Length % 4 != 0) st.WriteByte(0); }
        void Begin(string name) { Word(1); st.Write(Encoding.ASCII.GetBytes(name + "\0")); Pad(); }
        void Prop(string name, byte[] value)
        {
            if (!names.TryGetValue(name, out int off)) { off = (int)strings.Length; names[name] = off; strings.Write(Encoding.ASCII.GetBytes(name + "\0")); }
            Word(3); Word((uint)value.Length); Word((uint)off); st.Write(value); Pad();
        }
        byte[] Str(string v) => Encoding.ASCII.GetBytes(v + "\0");
        Begin("");
        Prop("description", Str("board FIT"));
        Begin("images");
        foreach ((string name, string type, byte[] data) in images)
        {
            Begin(name);
            Prop("type", Str(type)); Prop("compression", Str("none")); Prop("data", data);
            Begin("hash-1"); Prop("algo", Str("sha256")); Prop("value", System.Security.Cryptography.SHA256.HashData(data)); Word(2);
            Word(2);
        }
        Word(2);
        Begin("configurations"); Prop("default", Str("conf-1")); Begin("conf-1"); Prop("description", Str("board")); Word(2); Word(2);
        Word(2);
        Word(9);
        int offStruct = 56, offStrings = offStruct + (int)st.Length, total = offStrings + (int)strings.Length;
        byte[] dtb = new byte[total];
        uint[] header = { 0xD00DFEED, (uint)total, (uint)offStruct, (uint)offStrings, 40, 17, 16, 0, (uint)strings.Length, (uint)st.Length };
        for (int i = 0; i < header.Length; i++) BigEndian(header[i]).CopyTo(dtb, 4 * i);
        st.ToArray().CopyTo(dtb, offStruct);
        strings.ToArray().CopyTo(dtb, offStrings);
        return dtb;
    }

    private static NodeItem Item(MainViewModel vm, string name) => vm.Items.FirstOrDefault(i => i.Name == name) ?? throw new InvalidOperationException($"'{name}' is not in the list: {string.Join(", ", vm.Items.Select(i => i.Name))}");

    private static void Select(MainViewModel vm, params string[] names) => vm.SetSelection(names.Select(n => Item(vm, n)));

    /// <summary>
    /// Prints "Type|Property|PropertyType" for every public property of the view models and the
    /// model classes they expose. tests/check-bindings.py compares the XAML bindings with this list.
    /// </summary>
    private static int DumpProperties()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        foreach (Type t in typeof(MainViewModel).Assembly.GetExportedTypes()) queue.Enqueue(t);
        foreach (Type t in typeof(FsNode).Assembly.GetExportedTypes()) queue.Enqueue(t);
        while (queue.Count > 0)
        {
            Type t = queue.Dequeue();
            if (!seen.Add(t) || t.IsGenericTypeDefinition) continue;
            foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                Type pt = p.PropertyType;
                Console.WriteLine($"{Name(t)}|{p.Name}|{Name(pt)}");
                // Follow collections to their element type so that item templates can be checked too.
                foreach (Type i in pt.GetInterfaces().Append(pt))
                    if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    {
                        Console.WriteLine($"{Name(t)}|{p.Name}[]|{Name(i.GetGenericArguments()[0])}");
                        queue.Enqueue(i.GetGenericArguments()[0]);
                    }
                if (pt.Assembly == typeof(MainViewModel).Assembly || pt.Assembly == typeof(FsNode).Assembly) queue.Enqueue(pt);
            }
        }
        return 0;

        static string Name(Type t) => t.IsGenericType
            ? t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(",", t.GetGenericArguments().Select(Name)) + ">"
            : t.IsNested ? t.DeclaringType!.Name + "." + t.Name : t.Name;
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--dump-properties") return DumpProperties();
        _dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "fsforge-uitest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);

        await Test("open, list, sort, navigate", async () =>
        {
            var (vm, d, _, _) = NewVm();
            Check(vm.NoDocument && !vm.SaveCommand.CanExecute(null) && vm.OpenCommand.CanExecute(null), "initial command state");
            Check(await vm.OpenPathAsync(MakeImage("a.sqfs")), "open failed: " + string.Join("; ", d.Messages));
            Check(vm.HasDocument && vm.CurrentPath == "/" && !vm.IsModified, "document state after open");
            Check(vm.Title.StartsWith("a.sqfs — "), "title: " + vm.Title);
            Check(string.Join(",", vm.Items.Select(i => i.Name)) == "bin,dev,etc,usr,lib", "folders first, then by name: " + string.Join(",", vm.Items.Select(i => i.Name)));
            Check(vm.TreeRoots.Count == 1 && string.Join(",", vm.TreeRoots[0].Children.Select(c => c.Name)) == "bin,dev,etc,usr", "folder tree");
            Check(vm.Settings.RecentFiles.Count == 1 && vm.RecentFiles.Count == 1, "recent files");

            await vm.OpenItemCommand.ExecuteAsync(Item(vm, "etc"));
            Check(vm.CurrentPath == "/etc" && vm.Breadcrumbs.Count == 2 && vm.Items.Count == 4, "navigation into /etc");
            Check(vm.Items[0].Name == "Banner" && Item(vm, "hostname").Owner == "www (33) : www-data (33)", "name order and owner names: " + Item(vm, "hostname").Owner);
            vm.SortBy("Size");
            Check(vm.Items[0].Name == "Banner" && vm.Items[^1].Name == "passwd", "sort by size");
            vm.SortBy("Size");
            Check(vm.Items[0].Name == "passwd", "sort descending");
            vm.SortBy("Name");

            vm.UpCommand.Execute(null);
            Check(vm.CurrentPath == "/" && vm.Selection.Count == 1 && vm.Selection[0].Name == "etc", "up selects the folder we came from");
            vm.BackCommand.Execute(null);
            Check(vm.CurrentPath == "/etc", "back");
            vm.BackCommand.Execute(null);
            vm.ForwardCommand.Execute(null);
            Check(vm.CurrentPath == "/etc", "forward");
            vm.CurrentPath = "/usr/lib";
            Check(vm.CurrentPath == "/usr/lib" && vm.Items.Count == 30, "typed path");
            vm.CurrentPath = "/nope";
            Check(vm.CurrentPath == "/usr/lib" && d.Notifications.Any(n => n.Contains("Путь не найден")), "bad path keeps the folder");

            // A symlink to a directory opens the directory.
            vm.CurrentPath = "/";
            await vm.OpenItemCommand.ExecuteAsync(Item(vm, "lib"));
            Check(vm.CurrentPath == "/usr/lib", "symlink to a directory: " + vm.CurrentPath);

            vm.CurrentPath = "/";
            vm.SearchEverywhere = true;
            vm.SearchText = "lib1?.so";
            Check(vm.Items.Count == 10 && vm.Items[0].Location == "/usr/lib" && vm.ShowLocationColumn, "wildcard search everywhere: " + vm.Items.Count);
            vm.SearchText = "BUSY";
            Check(vm.Items.Count == 1 && vm.Items[0].Name == "busybox", "case-insensitive search");
            vm.SearchText = "";
            Check(vm.Items.Count == 5, "search cleared");
            vm.Dispose();
        });

        await Test("create, rename, delete, clipboard, properties", async () =>
        {
            var (vm, d, shell, _) = NewVm();
            await vm.OpenPathAsync(MakeImage("b.sqfs"));
            d.OnDialog = o =>
            {
                if (o is not NewItemViewModel n) return false;
                n.Name = "etc";
                Check(!n.Accept() && n.HasError, "a duplicate name must be rejected");
                n.Name = "opt";
                n.Mode = "0750";
                return n.Accept();
            };
            vm.NewFolderCommand.Execute(null);
            Check(vm.IsModified && Item(vm, "opt").Octal == "0750" && vm.Selection.Single().Name == "opt", "new folder");
            Check(vm.TreeRoots[0].Children.Any(c => c.Name == "opt"), "new folder appears in the tree");
            Check(vm.Title.Contains('•'), "modified marker in the title");

            d.OnDialog = o =>
            {
                var n = (NewItemViewModel)o;
                n.Kind = NewItemKind.Symlink;
                n.Name = "tmp";
                Check(!n.Accept(), "a link without a target must be rejected");
                n.Target = "/var/tmp";
                return n.Accept();
            };
            vm.NewSymlinkCommand.Execute(null);
            Check(Item(vm, "tmp").Details == "→ /var/tmp", "new symlink");

            d.OnDialog = o =>
            {
                var p = (PromptViewModel)o;
                p.Text = "a/b";
                Check(!p.Accept(), "a name with a slash must be rejected");
                p.Text = "option";
                return p.Accept();
            };
            Select(vm, "opt");
            vm.RenameCommand.Execute(null);
            Check(vm.Items.Any(i => i.Name == "option") && vm.Items.All(i => i.Name != "opt"), "rename");

            // Copy /etc into /option, then cut one file out of the copy.
            Select(vm, "etc");
            vm.CopyCommand.Execute(null);
            await vm.OpenItemCommand.ExecuteAsync(Item(vm, "option"));
            await vm.PasteCommand.ExecuteAsync();
            Check(vm.Items.Single().Name == "etc" && vm.Document!.Tree.Resolve("/option/etc/passwd") is not null, "paste copy");
            await vm.PasteCommand.ExecuteAsync();
            Check(vm.Items.Count == 2 && vm.Items.Any(i => i.Name == "etc (2)"), "second paste gets a new name");
            await vm.OpenItemCommand.ExecuteAsync(Item(vm, "etc"));
            Select(vm, "passwd", "group");
            vm.CutCommand.Execute(null);
            vm.CurrentPath = "/option";
            await vm.PasteCommand.ExecuteAsync();
            Check(vm.Document!.Tree.Resolve("/option/passwd") is not null && vm.Document.Tree.Resolve("/option/etc/passwd") is null, "cut and paste moves");
            Check(!vm.PasteCommand.CanExecute(null), "the clipboard is empty after a move");

            // Copying a folder into itself is refused.
            vm.CurrentPath = "/";
            Select(vm, "option");
            vm.CopyCommand.Execute(null);
            vm.CurrentPath = "/option/etc";
            await vm.PasteCommand.ExecuteAsync();
            Check(d.Messages.Any(m => m.Contains("внутрь самой себя")), "copy into itself must be reported");

            // Recursive chmod/chown through the properties dialog.
            vm.CurrentPath = "/";
            Select(vm, "option");
            d.OnDialog = o =>
            {
                var p = (PropertiesViewModel)o;
                Check(p.Octal == "0750" && p.OwnerExec && !p.OtherRead && p.Symbolic == "rwxr-x---", "properties show the mode: " + p.Symbolic);
                p.OtherRead = true;
                Check(p.Octal == "0754", "check box updates the octal text: " + p.Octal);
                p.Octal = "0700";
                Check(!p.GroupRead && !p.OtherRead, "octal text updates the check boxes");
                p.Uid = "1000";
                p.Gid = "abc";
                Check(!p.Accept() && p.Error!.Contains("GID"), "a bad GID must be rejected");
                p.Gid = "1000";
                p.Recursive = true;
                p.RecursiveTargets = FsTree.Targets.Files;
                p.Time = "2024-01-02 03:04:05";
                return p.Accept();
            };
            vm.PropertiesCommand.Execute(null);
            FsTree tree = vm.Document!.Tree;
            Check(tree.Resolve("/option/passwd")!.Inode is { Mode: 0x1C0, Uid: 1000, Gid: 1000 }, "recursive chmod on files");
            Check(tree.Resolve("/option")!.Inode is { Mode: 0x1E8, Uid: 1000 }, "folders keep their mode when only files are targeted, but get the owner");

            // Extended attributes and a symlink target.
            vm.CurrentPath = "/bin";
            Select(vm, "busybox");
            d.OnDialog = o =>
            {
                var p = (PropertiesViewModel)o;
                Check(p.LinksText.StartsWith("2"), "hard link count is shown");
                p.AddXattrCommand.Execute(null);
                p.Xattrs[^1].Name = "security.capability";
                p.Xattrs[^1].Value = "0x0100000200200000";
                p.AddXattrCommand.Execute(null);
                p.Xattrs[^1].Name = "nonamespace";
                Check(!p.Accept(), "an attribute without a namespace must be rejected");
                p.Xattrs[^1].Name = "user.note";
                p.Xattrs[^1].Value = "hello\\0";
                return p.Accept();
            };
            vm.PropertiesCommand.Execute(null);
            FsInode bb = tree.Resolve("/bin/busybox")!.Inode;
            Check(bb.XAttrs!["security.capability"].Length == 8 && bb.XAttrs["user.note"].AsSpan().SequenceEqual("hello\0"u8), "extended attributes were stored");

            Select(vm, "sh");
            d.OnDialog = o =>
            {
                var p = (PropertiesViewModel)o;
                Check(p.IsSymlink && p.Target == "busybox" && !p.CanEditMode, "symlink properties");
                p.Target = "/bin/ash";
                return p.Accept();
            };
            vm.PropertiesCommand.Execute(null);
            Check(Item(vm, "sh").Details == "→ /bin/ash", "link target changed");

            vm.CopyPathCommand.Execute(null);
            Check(shell.Clipboard == "/bin/sh", "copy path: " + shell.Clipboard);

            // Delete with confirmation, including the folder being shown elsewhere in history.
            vm.CurrentPath = "/";
            Select(vm, "option", "tmp");
            d.Answers.Enqueue(DialogAnswer.Cancel);
            await vm.DeleteCommand.ExecuteAsync();
            Check(tree.Resolve("/option") is not null, "a cancelled delete must keep the objects");
            Select(vm, "option", "tmp");
            d.Answers.Enqueue(DialogAnswer.Primary);
            await vm.DeleteCommand.ExecuteAsync();
            Check(tree.Resolve("/option") is null && tree.Resolve("/tmp") is null && vm.TreeRoots[0].Children.All(c => c.Name != "option"), "delete");
            vm.BackCommand.Execute(null);
            Check(vm.CurrentPath == "/bin", "back skips folders that no longer exist: " + vm.CurrentPath);
            vm.Dispose();
        });

        await Test("add files by drop, conflicts, extract", async () =>
        {
            var (vm, d, shell, _) = NewVm();
            string host = Path.Combine(_dir, "host");
            Directory.CreateDirectory(Path.Combine(host, "www", "cgi"));
            File.WriteAllText(Path.Combine(host, "hostname"), "newboard\n");
            File.WriteAllText(Path.Combine(host, "www", "index.html"), "<html/>");
            File.WriteAllText(Path.Combine(host, "www", "cgi", "run.sh"), "#!/bin/sh\n");

            // A folder dropped on the empty window becomes a new image.
            d.Answers.Enqueue(DialogAnswer.Primary);
            await vm.HandleDropAsync(new[] { Path.Combine(host, "www") });
            Check(vm.HasDocument && vm.Document!.FilePath is null && vm.Items.Count == 2, "new image from a dropped folder");
            Check(Item(vm, "cgi").IsDirectory && vm.Document!.Tree.Resolve("/cgi/run.sh")!.Inode.Mode == 0x1ED, "scripts become executable");
            // An image dropped on an opened image: the user chooses to open it and to drop the unsaved new image.
            string img = MakeImage("c.sqfs");
            d.Answers.Enqueue(DialogAnswer.Primary);            // "open" rather than "add as a file"
            d.Answers.Enqueue(DialogAnswer.Secondary);          // do not save the new image
            await vm.HandleDropAsync(new[] { img });
            Check(vm.Document!.FilePath == img, "an image dropped on an opened image can replace it");
            FsTree tree0 = vm.Document.Tree;

            vm.CurrentPath = "/etc";
            d.Answers.Enqueue(DialogAnswer.Primary);            // replace the existing hostname
            await vm.HandleDropAsync(new[] { Path.Combine(host, "hostname"), Path.Combine(host, "www") });
            FsTree tree = tree0;
            Check(tree.Resolve("/etc/hostname")!.Inode.Size == 9 && tree.Resolve("/etc/www/cgi/run.sh") is not null, "dropped files were added");
            Check(vm.Selection.Count == 2 && vm.IsModified, "added objects are selected");
            Check(d.Asked.Contains("Объекты с такими именами уже существуют"), "the conflict question was asked");

            d.Answers.Enqueue(DialogAnswer.Secondary);          // keep both
            await vm.HandleDropAsync(new[] { Path.Combine(host, "hostname") });
            Check(tree.Resolve("/etc/hostname (2)") is not null, "keep both adds a renamed copy");
            d.Answers.Enqueue(DialogAnswer.Cancel);
            await vm.HandleDropAsync(new[] { Path.Combine(host, "hostname") });
            Check(vm.Items.Count(i => i.Name.StartsWith("hostname")) == 2, "cancel adds nothing");

            // Extraction of a selection and of the whole image.
            string outDir = Path.Combine(_dir, "out-sel");
            Select(vm, "www", "passwd");
            d.FolderPicks.Enqueue(outDir);
            await vm.ExtractCommand.ExecuteAsync();
            Check(File.Exists(Path.Combine(outDir, "www", "index.html")) && File.Exists(Path.Combine(outDir, "passwd")), "extract selection");
            Check(File.Exists(Path.Combine(outDir, ".fsforge-meta.json")) && shell.Folders.Contains(outDir), "manifest written and folder opened");
            string outAll = Path.Combine(_dir, "out-all");
            d.FolderPicks.Enqueue(outAll);
            await vm.ExtractAllCommand.ExecuteAsync();
            Check(File.Exists(Path.Combine(outAll, "bin", "busybox")) && File.ReadAllText(Path.Combine(outAll, "bin", "sh")) == "busybox", "extract all");

            IReadOnlyList<string> dragged = vm.ExtractForDrag(new[] { tree.Resolve("/etc/passwd")!, tree.Resolve("/usr")! });
            Check(dragged.Count == 2 && dragged.All(p => File.Exists(p) || Directory.Exists(p)), "drag-out extraction");
            vm.Dispose();
            Check(!dragged.Any(p => File.Exists(p) || Directory.Exists(p)), "temporary files are removed on exit");
        });

        await Test("save in place, save as other formats, archives", async () =>
        {
            var (vm, d, _, _) = NewVm();
            string img = MakeImage("d.sqfs");
            await vm.OpenPathAsync(img);
            vm.CurrentPath = "/usr/lib";
            Select(vm, "lib00.so", "lib01.so");
            d.Answers.Enqueue(DialogAnswer.Primary);
            await vm.DeleteCommand.ExecuteAsync();
            vm.TreeRoots[0].Children.First(c => c.Name == "usr").IsExpanded = true;
            Check(vm.SaveCommand.CanExecute(null), "save must be enabled after a change");
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            Check(!vm.IsModified && vm.CurrentPath == "/usr/lib" && vm.Items.Count == 28, "the view is restored after saving: " + vm.CurrentPath);
            Check(vm.TreeRoots[0].Children.First(c => c.Name == "usr").IsExpanded, "expanded folders are restored");
            Check(!vm.SaveCommand.CanExecute(null) && d.Notifications.Any(n => n.Contains("Образ сохранён")), "state after saving");
            Check(vm.Document!.SquashFs!.Compression == SquashCompression.Gzip, "the compression of the source is kept");
            Check(!File.Exists(img + ".fsforge-tmp") && !File.Exists(img + ".bak"), "no leftovers");

            // Save as ext4 of a fixed size.
            string ext = Path.Combine(_dir, "d.ext4");
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsSquash && s.SquashCompression == SquashCompression.Gzip, "dialog starts with the source format");
                s.Format = SaveFormat.Ext4;
                Check(s.IsExt && !s.CanKeepSize && s.SizeMode == ExtSizeMode.Minimal && s.Extension == ".ext4", "ext defaults");
                s.SizeMode = ExtSizeMode.Fixed;
                s.SizeMiB = "x";
                Check(!s.Accept(), "a bad size must be rejected");
                s.SizeMiB = "16";
                s.Label = "rootfs";
                return s.Accept();
            };
            d.SavePicks.Enqueue(ext);
            Check(await vm.SaveAsAsync(), "save as ext4 failed: " + string.Join("; ", d.Messages));
            Check(d.LastSuggestedName == "d.ext4", "suggested name: " + d.LastSuggestedName);
            Check(vm.Document!.Format == ImageFormat.Ext && vm.Document.Ext!.SizeBytes == 16 << 20 && vm.Document.Ext.Label == "rootfs", "ext4 image");
            Check(vm.Document.FilePath == ext && vm.CurrentPath == "/usr/lib" && vm.Title.StartsWith("d.ext4"), "document switched to the new file");

            // Saving the ext image again keeps its geometry.
            vm.CurrentPath = "/etc";
            Select(vm, "Banner");
            d.Answers.Enqueue(DialogAnswer.Primary);
            await vm.DeleteCommand.ExecuteAsync();
            Guid uuid = vm.Document.Ext!.Uuid;
            Check(await vm.SaveAsync(), "second save failed: " + string.Join("; ", d.Messages));
            Check(vm.Document!.Ext!.SizeBytes == 16 << 20 && vm.Document.Ext.Uuid == uuid && vm.Document.Tree.Resolve("/etc/Banner") is null, "geometry and UUID are kept");

            // Export /etc as tar.xz without touching the document, then import it elsewhere.
            string tar = Path.Combine(_dir, "etc.tar.xz");
            vm.CurrentPath = "/";
            Select(vm, "etc");
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.FormatLocked && s.IsTar, "export dialog is fixed to tar");
                s.TarCompression = ArchiveCompression.Xz;
                s.XzEffort = 0;
                return s.Accept();
            };
            d.SavePicks.Enqueue(tar);
            await vm.ExportArchiveCommand.ExecuteAsync();
            Check(File.Exists(tar) && d.LastSuggestedName == "etc.tar.xz" && !vm.IsModified && vm.Document.FilePath == ext, "export leaves the document alone");

            vm.CurrentPath = "/usr";
            d.OpenPicks.Enqueue(tar);
            await vm.ImportArchiveCommand.ExecuteAsync();
            Check(vm.Document.Tree.Resolve("/usr/passwd") is not null && vm.Document.Tree.Resolve("/usr/hostname")!.Inode.Uid == 33 && vm.IsModified, "archive imported into /usr");

            // Convert to rootfs.tar.gz.
            string tgz = Path.Combine(_dir, "rootfs.tar.gz");
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                s.Format = SaveFormat.Tar;
                return s.Accept();
            };
            d.SavePicks.Enqueue(tgz);
            Check(await vm.SaveAsAsync(), "save as tar.gz failed: " + string.Join("; ", d.Messages));
            Check(vm.Document!.Format == ImageFormat.Tar && vm.Document.Tar!.Compression == ArchiveCompression.Gzip && vm.Document.Tree.Resolve("/usr/passwd") is not null, "tar.gz document");
            Check(vm.Document.Tree.Resolve("/lost+found") is not null, "content of the ext image, including lost+found, is carried over");

            // Unsaved changes: closing asks, "cancel" keeps the document.
            vm.CurrentPath = "/";
            Select(vm, "dev");
            d.Answers.Enqueue(DialogAnswer.Primary);
            await vm.DeleteCommand.ExecuteAsync();
            d.Answers.Enqueue(DialogAnswer.Cancel);
            await vm.CloseCommand.ExecuteAsync();
            Check(vm.HasDocument, "cancel keeps the document open");
            d.Answers.Enqueue(DialogAnswer.Primary);            // save, then close
            await vm.CloseCommand.ExecuteAsync();
            Check(vm.NoDocument && vm.Items.Count == 0 && vm.Title == "FsForge", "closed after saving");
            using ImageDocument check = ImageDocument.Open(tgz, _dir);
            Check(check.Tree.Resolve("/dev") is null, "the save made on close reached the file");
            vm.Dispose();
        });

        await Test("external editor round trip, replace content, errors", async () =>
        {
            var (vm, d, shell, store) = NewVm();
            await vm.OpenPathAsync(MakeImage("e.sqfs"));
            vm.CurrentPath = "/etc";
            await vm.OpenItemCommand.ExecuteAsync(Item(vm, "hostname"));
            Check(shell.Files.Count == 1 && File.ReadAllText(shell.Files[0]) == "board\n", "the file was extracted and opened");
            await vm.CheckExternalEditsAsync();
            Check(d.Asked.Count == 0, "an unchanged file must not trigger a question");
            File.WriteAllText(shell.Files[0], "edited-board\n");
            d.Answers.Enqueue(DialogAnswer.Primary);
            await vm.CheckExternalEditsAsync();
            FsInode inode = vm.Document!.Tree.Resolve("/etc/hostname")!.Inode;
            using (var r = new StreamReader(inode.Data!.OpenRead())) Check(r.ReadToEnd() == "edited-board\n", "edited content is in the image");
            Check(vm.IsModified && inode is { Uid: 33, Mode: 0x1A4 }, "attributes survive an edit");
            await vm.CheckExternalEditsAsync();
            Check(d.Asked.Count == 1, "the same edit is offered only once");
            File.Delete(shell.Files[0]);                         // the image must not depend on the temporary file

            string repl = Path.Combine(_dir, "banner.txt");
            File.WriteAllText(repl, "replaced banner\n");
            Select(vm, "Banner");
            d.FilePicks.Enqueue(new[] { repl });
            await vm.ReplaceContentCommand.ExecuteAsync();
            Check(vm.Document.Tree.Resolve("/etc/Banner")!.Inode.Size == 16, "content replaced");

            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            using (var r = new StreamReader(vm.Document!.Tree.Resolve("/etc/hostname")!.Inode.Data!.OpenRead())) Check(r.ReadToEnd() == "edited-board\n", "edit persisted");

            // A host file that changes after it was added is reported at save time, and nothing is lost.
            File.WriteAllText(repl, "v1");
            vm.CurrentPath = "/";
            await vm.HandleDropAsync(new[] { repl });
            File.WriteAllText(repl, "version two is longer");
            long before = new FileInfo(vm.Document.FilePath!).Length;
            Check(!await vm.SaveAsync() && d.Messages.Any(m => m.Contains("has changed since")), "a changed source file must fail the save: " + string.Join("; ", d.Messages));
            Check(vm.HasDocument && vm.IsModified && new FileInfo(vm.Document.FilePath!).Length == before, "a failed save keeps the document and the old file");

            // Opening garbage reports an error and keeps the current document.
            string junk = Path.Combine(_dir, "junk.bin");
            File.WriteAllBytes(junk, new byte[5000]);
            d.Answers.Enqueue(DialogAnswer.Secondary);          // discard changes
            Check(!await vm.OpenPathAsync(junk) && vm.HasDocument, "garbage must not replace the document");

            // Settings survive a round trip through the file and repair bad values.
            AppSettings s = vm.Settings.Clone();
            s.Theme = AppTheme.Dark;
            s.SquashFs.Compression = SquashCompression.Zstd;
            s.DefaultFileMode = "bad";
            bool appearance = false;
            vm.AppearanceChanged += () => appearance = true;
            vm.ApplySettings(SettingsStore.Sanitize(s));
            AppSettings loaded = store.Load();
            Check(appearance && loaded.Theme == AppTheme.Dark && loaded.SquashFs.Compression == SquashCompression.Zstd && loaded.DefaultFileMode == "0644", "settings round trip");
            Check(loaded.RecentFiles.Count == 1, "recent files are persisted");
            File.WriteAllText(store.FilePath, "{ not json");
            Check(store.Load().Theme == AppTheme.System, "a damaged settings file gives the defaults");

            d.OnDialog = o =>
            {
                var sv = (SettingsViewModel)o;
                sv.Settings.DefaultFileMode = "99";
                Check(!sv.Accept(), "bad default mode must be rejected");
                sv.ResetCommand.Execute(null);
                Check(sv.Settings.Theme == AppTheme.System && sv.Settings.RecentFiles.Count == 1, "reset keeps the history");
                sv.Settings.ConfirmDelete = false;
                return sv.Accept();
            };
            vm.SettingsCommand.Execute(null);
            Check(!vm.Settings.ConfirmDelete && store.Load().ConfirmDelete == false, "settings dialog result is applied and saved");

            d.OnDialog = o => o is ImageInfoViewModel i && i.Rows.Count > 5;
            vm.ImageInfoCommand.Execute(null);
            vm.Dispose();
        });

        await Test("disk image: partition choice, saving keeps the disk, filesystem alone on request", async () =>
        {
            var (vm, d, _, _) = NewVm();
            string disk = MakeDisk("sd.img.gz", out long rootOffset);
            byte[] before = Gunzip(disk);

            // Two partitions can be edited: the user is asked, the root filesystem is preselected.
            int asked = 0;
            d.OnDialog = o =>
            {
                var c = (PartitionChoiceViewModel)o;
                asked++;
                Check(c.Rows.Count == 2 && c.Selected is { Partition.Number: 2, Content: "SquashFS", IsEditable: true }, "partition list: " + string.Join("; ", c.Rows.Select(r => r.Name + " " + r.Content)));
                Check(c.Rows[0] is { Name: "Раздел 1", Content: "ext2 «boot»" } && c.Summary.Contains("MBR"), "boot partition row: " + c.Rows[0].Content);
                c.Selected = null;
                Check(!c.Accept() && c.HasError, "accepted without a selection");
                c.Selected = c.Rows[1];
                return c.Accept();
            };
            Check(await vm.OpenPathAsync(disk), "open failed: " + string.Join("; ", d.Messages));
            Check(asked == 1 && vm.Document!.Container is { Partition.Number: 2 } && !vm.Document.SavesFilesystemOnly, "the chosen partition was not opened");
            Check(vm.DocumentInfo.StartsWith("образ диска MBR · раздел 2 из 2 · SquashFS"), "status line: " + vm.DocumentInfo);
            Check(!d.Notifications.Any(n => n.Contains("предупрежден")), "a disk image must open without warnings: " + string.Join("; ", d.Notifications));

            d.OnDialog = o => o is ImageInfoViewModel i && i.Rows.Any(r => r.Label.StartsWith("▸ Раздел 2")) && i.Rows.Any(r => r.Label == "Раздел 1") && i.Rows.Any(r => r.Label == "Сохранение");
            int infos = d.Messages.Count;
            vm.ImageInfoCommand.Execute(null);

            // Ctrl+S: no questions, the file stays a gzipped disk image with everything else untouched.
            vm.CurrentPath = "/etc";
            Select(vm, "Banner");
            d.Answers.Enqueue(DialogAnswer.Primary);
            await vm.DeleteCommand.ExecuteAsync();
            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog on save: " + o.GetType().Name);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            Check(vm.Document!.Container is { Partition.Number: 2 } && vm.Document.OuterCompression == ArchiveCompression.Gzip, "the saved file is not a gzipped disk image");
            Check(vm.CurrentPath == "/etc" && vm.Items.Count == 3 && !vm.IsModified, "view after saving");
            byte[] after = Gunzip(disk);
            Check(after.AsSpan(0, (int)rootOffset).SequenceEqual(before.AsSpan(0, (int)rootOffset)), "the partition table or the boot partition changed");
            Check(after.Length >= before.Length && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan((int)rootOffset)) == 0x73717368, "no SquashFS in partition 2");

            // Save as: the dialog explains the disk image and proposes the same name.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.ShowContainerOptions && s.KeepContainer && s.WritesDisk && s.Extension == ".img.gz", "disk options in the save dialog: " + s.Extension);
                Check(s.ContainerSummary.Contains("sd.img.gz") && s.ContainerSummary.Contains("раздел 2") && s.ContainerNote.Contains("будет увеличен"), "texts: " + s.ContainerSummary + " / " + s.ContainerNote);
                s.Format = SaveFormat.Tar;
                Check(!s.WritesDisk && s.Extension == ".tar.gz", "a tar archive is never a disk image");
                s.Format = SaveFormat.Ext4;
                Check(s.WritesDisk && s.HasExtSizeNote, "ext4 inside the partition needs the size note");
                s.SizeMode = ExtSizeMode.Fixed;
                s.SizeMiB = "1";
                Check(s.Accept(), "fixed size inside the partition: " + s.Error);
                s.Format = SaveFormat.SquashFs;
                return s.Accept();
            };
            string copy = Path.Combine(_dir, "sd-copy.img");
            d.SavePicks.Enqueue(copy);
            Check(await vm.SaveAsAsync(), "save as failed: " + string.Join("; ", d.Messages));
            Check(d.LastSuggestedName == "sd.img.gz" && d.LastSaveWasDisk, "suggested name for a disk image: " + d.LastSuggestedName);
            Check(vm.Document!.Container is not null && vm.Document.OuterCompression is null && File.ReadAllBytes(copy).AsSpan(0, (int)rootOffset).SequenceEqual(before.AsSpan(0, (int)rootOffset)), "plain disk image");

            // The filesystem alone, when the switch is turned off.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                s.KeepContainer = false;
                Check(!s.WritesDisk && s.Extension == ".squashfs" && s.ContainerNote.Contains("только файловая система"), "filesystem-only mode: " + s.ContainerNote);
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "sd-root.squashfs"));
            Check(await vm.SaveAsAsync(), "save as filesystem failed: " + string.Join("; ", d.Messages));
            Check(d.LastSuggestedName == "sd-copy.squashfs" && !d.LastSaveWasDisk && vm.Document!.Container is null && vm.Document.EmbeddedOffset == 0, "filesystem saved alone: " + d.LastSuggestedName);
            Check(!vm.DocumentInfo.Contains("образ диска"), "status line of a plain image: " + vm.DocumentInfo);

            // Cancelling the partition dialog leaves the current document alone.
            d.OnDialog = o => o is PartitionChoiceViewModel ? false : throw new InvalidOperationException("unexpected dialog");
            Check(!await vm.OpenPathAsync(disk) && vm.Document!.FilePath!.EndsWith("sd-root.squashfs"), "a cancelled choice changed the document");
            Check(d.Notifications.Any(n => n.Contains("Отменено")) && d.Messages.Count == infos, "cancelling is not an error: " + string.Join("; ", d.Messages));

            // A dropped disk image is recognised as an image.
            d.Answers.Enqueue(DialogAnswer.Cancel);
            await vm.HandleDropAsync(new[] { disk });
            Check(d.Asked[^1] == "Что сделать с файлом?", "a dropped disk image was not recognised: " + d.Asked[^1]);
            vm.Dispose();
        });

        await Test("UBI image and UBIFS: volume choice, saving the UBI image, a volume alone, a new UBI image", async () =>
        {
            var (vm, d, _, _) = NewVm();
            // The tree of the usual test image as the UBIFS root volume, next to a kernel and a small data volume.
            byte[] root, data, kernel = new byte[200000].Select((_, i) => (byte)(i * 7 + 1)).ToArray();
            using (ImageDocument source = ImageDocument.Open(MakeImage("u.root"), _dir))
                root = FsForge.Cli.UbifsImageBuilder.FromTree(source.Tree).ToArray();
            using (var small = new FsTree(0x1ED, 1700000000))
            {
                small.CreateFile(small.Root, "marker", new MemoryFileData(new byte[10]));
                data = FsForge.Cli.UbifsImageBuilder.FromTree(small).ToArray();
            }
            string ubi = Path.Combine(_dir, "nand.ubi");
            File.WriteAllBytes(ubi, FsForge.Cli.UbifsImageBuilder.WrapUbi(new[]
            {
                new FsForge.Cli.UbifsImageBuilder.Volume("kernel", kernel, Static: true),
                new FsForge.Cli.UbifsImageBuilder.Volume("rootfs", root),
                new FsForge.Cli.UbifsImageBuilder.Volume("rootfs_data", data),
            }));

            // Two volumes can be opened: the user is asked, the root filesystem is preselected.
            int asked = 0;
            d.OnDialog = o =>
            {
                var c = (PartitionChoiceViewModel)o;
                asked++;
                Check(c.Title == "Выбор тома" && c.Summary.Contains("образ UBI") && c.Rows.Count == 3, "volume dialog: " + c.Title + " / " + c.Summary);
                Check(c.Selected is { Name: "Том «rootfs» (1)", Content: "UBIFS", IsEditable: true, Partition: null }, "preselected volume: " + c.Selected?.Name);
                Check(c.Rows[0] is { Name: "Том «kernel» (0)", Content: "неизвестное содержимое", IsEditable: false } && c.Rows[0].Details.Contains("статический"), "kernel row: " + c.Rows[0].Content + " / " + c.Rows[0].Details);
                c.Selected = c.Rows[0];
                Check(!c.Accept() && c.Error!.Contains("В этом томе нет UBIFS"), "a volume without a filesystem was accepted");
                c.Selected = null;
                Check(!c.Accept() && c.Error == "Выберите том.", "accepted without a selection");
                c.Selected = c.Rows[1];
                return c.Accept();
            };
            Check(await vm.OpenPathAsync(ubi), "open failed: " + string.Join("; ", d.Messages));
            Check(asked == 1 && vm.Document is { Format: ImageFormat.Ubifs, Ubi.Volume.Name: "rootfs", SavesFilesystemOnly: false }, "the chosen volume was not opened");
            Check(vm.DocumentInfo.StartsWith("образ UBI · том «rootfs» (1) из 3 · UBIFS · zlib · блок стирания 124 КБ"), "status line: " + vm.DocumentInfo);
            Check(!vm.IsInfoOpen, "a UBI image must open without warnings: " + vm.InfoMessage);
            vm.CurrentPath = "/etc";
            Check(vm.Items.Count == 4 && vm.Document!.Tree.Resolve("/bin/ash")!.Inode.LinkCount == 2, "content of the volume");

            d.OnDialog = o => o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "▸ Том «rootfs» (1) (открыт)") && i.Rows.Any(r => r.Label == "Том «kernel» (0)")
                                  && i.Rows.Any(r => r.Label == "Состояние" && r.Value == "чистая") && i.Rows.Any(r => r.Label == "Сохранение" && r.Value.Contains("образ UBI целиком"));
            vm.ImageInfoCommand.Execute(null);

            // Ctrl+S: no questions, the file stays a UBI image with the other volumes as they were.
            Select(vm, "Banner");
            d.Answers.Enqueue(DialogAnswer.Primary);
            await vm.DeleteCommand.ExecuteAsync();
            d.OnDialog = o => o is PartitionChoiceViewModel ? throw new InvalidOperationException("the volume was asked for again") : throw new InvalidOperationException("unexpected dialog on save: " + o.GetType().Name);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Ubifs, Ubi.Volume.Name: "rootfs" } && vm.Document.FilePath == ubi && vm.Document.Ubi!.Image.Volumes.Count == 3, "the saved file is not the UBI image");
            Check(vm.CurrentPath == "/etc" && vm.Items.Count == 3 && !vm.IsModified && vm.Document!.Tree.Resolve("/etc/Banner") is null, "view after saving");
            FsForge.Core.Ubifs.UbiImage image = vm.Document!.Ubi!.Image;
            Check(image.OpenVolume(image.Volumes[0]).ReadBytes(0, kernel.Length).AsSpan().SequenceEqual(kernel) && image.Volumes[0].IsStatic, "the kernel volume changed");
            Check(image.Volumes[2] is { Name: "rootfs_data", IsReadable: true }, "the data volume: " + image.Volumes[2]);

            // Another volume without closing the file; the kernel volume holds no filesystem.
            Check(vm.Parts.Count == 3 && vm.Parts[0] is { Text: "Том «kernel» (0) · неизвестное содержимое", CanOpen: false } && vm.CurrentPart is { Text: "Том «rootfs» (1) · UBIFS" }
                  && vm.Parts[2] is { Name: "Том «rootfs_data» (2)", CanOpen: true }, "UBI parts: " + string.Join("; ", vm.Parts.Select(p => p.Text)));
            Check(await vm.SwitchPartAsync(vm.Parts[2]) && vm.Document is { Ubi.Volume.Name: "rootfs_data" } && vm.DocumentInfo.StartsWith("образ UBI · том «rootfs_data» (2) из 3"),
                "switch to rootfs_data: " + vm.DocumentInfo + " " + string.Join("; ", d.Messages));
            Check(await vm.SwitchPartAsync(vm.Parts[1]) && vm.Document is { Ubi.Volume.Name: "rootfs" } && vm.Document.Tree.Resolve("/etc/Banner") is null, "back to rootfs");
            vm.CurrentPath = "/etc";

            // Save as: the dialog explains the UBI image; the filesystem alone and a tar archive are separate files.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.ShowContainerOptions && s.IsUbiContainer && s.KeepContainer && s.WritesDisk && s.WritesUbi && s.Extension == ".ubi", "UBI options in the save dialog: " + s.Extension);
                Check(s.ContainerTitle == "Образ UBI" && s.ContainerToggleText.Contains("образ UBI целиком") && s.ContainerSummary.Contains("nand.ubi") && s.ContainerSummary.Contains("том «rootfs» (1)")
                      && s.ContainerNote.Contains("остальные тома переносятся"), "texts: " + s.ContainerSummary + " / " + s.ContainerNote);
                Check(s.Format == SaveFormat.Ubifs && s.IsUbifs && !s.CanEditUbifsGeometry && s.UbifsLebSize == "126976" && s.UbifsMinIoSize == "2048" && !s.ShowNewUbi, "UBIFS fields: " + s.UbifsLebSize);
                s.Format = SaveFormat.Tar;
                Check(!s.WritesDisk && !s.WritesUbi && s.Extension == ".tar.gz" && s.ContainerNote.Contains("образ UBI при этом не меняется"), "a tar archive is never a UBI image");
                s.Format = SaveFormat.Ubifs;
                s.KeepContainer = false;
                Check(!s.WritesDisk && s.Extension == ".ubifs" && s.CanEditUbifsGeometry && s.ShowNewUbi && s.ContainerNote.Contains("только файловая система"), "filesystem-only mode: " + s.ContainerNote);
                s.UbifsLebSize = "abc";
                Check(!s.Accept() && s.HasError, "a bad erase block size was accepted");
                s.UbifsLebSize = "100000";
                Check(!s.Accept() && s.Error!.Contains("multiple"), "an erase block that is no multiple of the page was accepted: " + s.Error);
                s.UbifsLebSize = "126976";
                return s.Accept();
            };
            string alone = Path.Combine(_dir, "nand-root.ubifs");
            d.SavePicks.Enqueue(alone);
            Check(await vm.SaveAsAsync(), "save as failed: " + string.Join("; ", d.Messages));
            Check(d.LastSuggestedName == "nand.ubifs" && !d.LastSaveWasDisk && vm.Document is { Format: ImageFormat.Ubifs, Ubi: null } && vm.Document.FilePath == alone, "filesystem saved alone: " + d.LastSuggestedName);
            Check(vm.DocumentInfo.StartsWith("UBIFS · zlib · блок стирания"), "status line of a plain UBIFS image: " + vm.DocumentInfo);

            // A plain UBIFS image is edited and saved in place like any other image.
            vm.CurrentPath = "/etc";
            Select(vm, "group");
            d.Answers.Enqueue(DialogAnswer.Primary);
            await vm.DeleteCommand.ExecuteAsync();
            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog: " + o.GetType().Name);
            Check(await vm.SaveAsync() && vm.Document is { Format: ImageFormat.Ubifs } && vm.Document.Tree.Resolve("/etc/group") is null && !vm.IsModified, "UBIFS saved in place: " + string.Join("; ", d.Messages));

            // A new UBI image around it: the erase block follows the flash parameters given in the dialog.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.ShowNewUbi && !s.NewUbi && !s.ShowUbiOptions && s.Extension == ".ubifs", "the new UBI switch");
                s.NewUbi = true;
                Check(s.ShowUbiOptions && s.WritesUbi && s.Extension == ".ubi" && !s.CanEditUbifsGeometry, "new UBI mode: " + s.Extension);
                s.UbiPebSize = "1000";
                Check(!s.Accept() && s.HasError, "a bad UBI erase block was accepted");
                s.UbiPebSize = "131072";
                s.UbiSubPageSize = "512";
                s.UbiVolumeName = "system";
                s.UbiAutoResize = false;
                return s.Accept();
            };
            string made = Path.Combine(_dir, "made.ubi");
            d.SavePicks.Enqueue(made);
            Check(await vm.SaveAsAsync(), "new UBI failed: " + string.Join("; ", d.Messages));
            Check(d.LastSuggestedName == "nand-root.ubi" && vm.Document is { Format: ImageFormat.Ubifs, Ubi.Volume: { Name: "system", AutoResize: false } } && vm.Document.Ubifs!.LebSize == 129024
                  && vm.Document.Ubi.Image.VidHeaderOffset == 512, "the new UBI image: " + vm.Document?.Ubifs?.LebSize);
            Check(vm.Document!.Tree.Resolve("/etc/group") is null && vm.Document.Tree.Resolve("/etc/passwd") is not null, "content of the new UBI image");

            // SquashFS can be converted to UBIFS, and UBIFS is among the formats of new images.
            Check(await vm.OpenPathAsync(MakeImage("to-ubifs.sqfs")), "open failed");
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.Formats.Any(f => f.Value == SaveFormat.Ubifs) && !s.IsUbifs, "UBIFS is offered as a target");
                s.Format = SaveFormat.Ubifs;
                Check(s.IsUbifs && s.Extension == ".ubifs" && s.UbifsCompression == FsForge.Core.Ubifs.UbifsCompression.Lzo && s.UbifsMaxLebCount == "0", "defaults of a new UBIFS image");
                s.UbifsCompression = FsForge.Core.Ubifs.UbifsCompression.Zstd;
                s.UbifsLebSize = "65408";
                s.UbifsMinIoSize = "1";
                s.SpaceFixup = true;
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "from-squash.ubifs"));
            Check(await vm.SaveAsAsync(), "SquashFS -> UBIFS failed: " + string.Join("; ", d.Messages));
            Check(d.LastSuggestedName == "to-ubifs.ubifs" && vm.Document is { Format: ImageFormat.Ubifs } && vm.Document.Ubifs is { LebSize: 65408, MinIoSize: 8, SpaceFixup: true, DefaultCompression: FsForge.Core.Ubifs.UbifsCompression.Zstd },
                "the converted image: " + vm.Document?.Ubifs?.LebSize);
            Check(new SettingsViewModel(vm.Settings.Clone(), d, Path.Combine(_dir, "s.json")).NewFormats.Any(f => f.Value == ImageFormat.Ubifs), "UBIFS as the format of new images");
            Check(SettingsStore.Sanitize(new AppSettings { Ubifs = new FsForge.Core.Ubifs.UbifsOptions { LebSize = 5 } }).Ubifs.LebSize == 126976, "bad UBIFS defaults are repaired");

            // A cancelled volume choice leaves the document alone; a dropped UBI image is recognised as an image.
            d.OnDialog = o => o is PartitionChoiceViewModel ? false : throw new InvalidOperationException("unexpected dialog");
            Check(!await vm.OpenPathAsync(ubi) && vm.Document!.FilePath!.EndsWith("from-squash.ubifs"), "a cancelled choice changed the document");
            d.Answers.Enqueue(DialogAnswer.Cancel);
            await vm.HandleDropAsync(new[] { ubi });
            Check(d.Asked[^1] == "Что сделать с файлом?", "a dropped UBI image was not recognised: " + d.Asked[^1]);

            try
            {
                L.Set(AppLanguage.English);
                using ImageDocument doc = ImageDocument.Open(ubi, _dir);
                var c = new PartitionChoiceViewModel("nand.ubi", doc.Ubi!.Image);
                Check(c.Title == "Choose a volume" && c.Rows[1] is { Name: "Volume “rootfs” (1)" } && c.Rows[0].Details.EndsWith("static"), "English volume dialog: " + c.Rows[1].Name + " / " + c.Rows[0].Details);
                var s = new SaveOptionsViewModel(doc, vm.Settings.DefaultSaveOptions(doc));
                Check(s.ContainerTitle == "UBI image" && s.ContainerNote.Contains("carried over unchanged") && !s.ContainerNote.Any(ch => ch is >= 'А' and <= 'я'), "English save dialog: " + s.ContainerNote);
            }
            finally
            {
                L.Set(AppLanguage.Russian);
            }
            vm.Dispose();
        });

        await Test("JFFS2: open a big-endian image, keep its layout on saving, convert it", async () =>
        {
            var (vm, d, _, _) = NewVm();
            // A big-endian JFFS2 image with 16 KiB erase blocks (stored uncompressed, so that it spans several), made from the test tree.
            string jffs2 = Path.Combine(_dir, "rootfs_data.jffs2");
            using (ImageDocument source = ImageDocument.Open(MakeImage("j.sqfs"), _dir))
            using (var fs = new FileStream(jffs2, FileMode.Create))
                ImageDocument.WriteTo(source.Tree, fs, new SaveOptions
                {
                    Format = ImageFormat.Jffs2,
                    Jffs2 = new FsForge.Core.Jffs2.Jffs2Options { BigEndian = true, EraseBlockSize = 16 * 1024, Compression = FsForge.Core.Jffs2.Jffs2Compression.None },
                });
            Check(await vm.OpenPathAsync(jffs2), "open failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Jffs2, Jffs2.BigEndian: true } && vm.DocumentInfo.Contains("JFFS2") && vm.DocumentInfo.Contains("big-endian"),
                "status line: " + vm.DocumentInfo);
            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Порядок байтов" && r.Value == "big-endian")
                                                                    && i.Rows.Any(r => r.Label == "Блок стирания" && r.Value == "16 КБ");
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of a JFFS2 image");

            // "Save as" starts with the layout of the source; the format cannot go into a container.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsJffs2 && s.Jffs2BigEndian && s.Jffs2EraseBlockSize == 16 * 1024 && s.Jffs2Cleanmarkers && s.Extension == ".jffs2"
                      && !s.ShowCommonXattrs && !s.WritesDisk && !s.HasJffs2Note, "the dialog does not start with the layout of the source");
                s.Jffs2Compression = FsForge.Core.Jffs2.Jffs2Compression.Lzo;
                s.Jffs2EraseBlockSize = 64 * 1024;
                return s.Accept();
            };
            string again = Path.Combine(_dir, "rootfs_data-lzo.jffs2");
            d.SavePicks.Enqueue(again);
            Check(await vm.SaveAsAsync(), "save as JFFS2 failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Jffs2, Jffs2: { BigEndian: true, EraseBlockSize: 65536, MainCompression: FsForge.Core.Jffs2.Jffs2Compression.Lzo } },
                "saved image: " + vm.DocumentInfo);
            Check(vm.Document!.Tree.Resolve("/etc/passwd") is not null && vm.Document.Tree.Resolve("/dev/null")!.Inode.DevMinor == 3, "content of the saved image");

            // Converting to SquashFS.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                s.Format = SaveFormat.SquashFs;
                Check(s.ShowCommonXattrs && s.Extension == ".squashfs", "SquashFS after JFFS2");
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "rootfs_data.squashfs"));
            Check(await vm.SaveAsAsync(), "save as SquashFS failed: " + string.Join("; ", d.Messages));
            Check(vm.Document!.Format == ImageFormat.SquashFs && vm.Document.Tree.Resolve("/bin/ash") is not null, "converted image");
            vm.Dispose();
        });

        await Test("EROFS: open an lzma image, keep its settings on saving, change them, convert it", async () =>
        {
            var (vm, d, _, _) = NewVm();
            string erofs = Path.Combine(_dir, "system.erofs");
            var uuid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
            using (ImageDocument source = ImageDocument.Open(MakeImage("e.sqfs"), _dir))
            using (var fs = new FileStream(erofs, FileMode.Create))
                ImageDocument.WriteTo(source.Tree, fs, new SaveOptions
                {
                    Format = ImageFormat.Erofs,
                    Erofs = new FsForge.Core.Erofs.ErofsOptions
                    {
                        Compression = FsForge.Core.Erofs.ErofsCompression.Lzma, LzmaEffort = 0, PclusterSize = 16 * 1024, VolumeName = "system", Uuid = uuid,
                    },
                });
            Check(await vm.OpenPathAsync(erofs), "open failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Erofs, Erofs.VolumeName: "system" } && vm.DocumentInfo.StartsWith("EROFS · lzma · кластер "), "status line: " + vm.DocumentInfo);
            Check(!vm.IsInfoOpen, "an EROFS image must open without warnings: " + vm.InfoMessage);
            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Сжатие" && r.Value.StartsWith("lzma, кластеры до "))
                                                                    && i.Rows.Any(r => r.Label == "Метка" && r.Value == "system")
                                                                    && i.Rows.Any(r => r.Label == "UUID" && r.Value == uuid.ToString());
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of an EROFS image");

            // "Save as" starts with the settings of the source; another method shows its own options.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsErofs && s.ErofsCompression == FsForge.Core.Erofs.ErofsCompression.Lzma && s.ErofsVolumeName == "system" && s.ErofsKeepUuid && s.CanKeepErofsUuid
                      && s.ShowErofsLzma && !s.ShowErofsLz4Level && s.ShowErofsCluster && s.HasErofsNote && s.ErofsNote.Contains("5.16") && s.Extension == ".erofs"
                      && s.ShowCommonXattrs && s.ShowAllRoot && !s.ShowNewUbi && !s.WritesDisk,
                    "the dialog does not start with the settings of the source");
                Check(s.ErofsPclusterSizes.Any(c => c.Value == s.ErofsPclusterSize), "the cluster size of the source is not in the list: " + s.ErofsPclusterSize);
                s.ErofsCompression = FsForge.Core.Erofs.ErofsCompression.Zstd;
                Check(s.ShowErofsZstd && !s.ShowErofsDeflate && !s.ShowErofsLzma && s.ErofsNote.Contains("6.10") && s.ErofsZstdLevel == 3, "options of zstd: " + s.ErofsNote);
                s.ErofsCompression = FsForge.Core.Erofs.ErofsCompression.Deflate;
                Check(s.ShowErofsDeflate && !s.ShowErofsZstd && s.ErofsNote.Contains("6.6") && s.ErofsDeflateLevel == 6, "options of deflate: " + s.ErofsNote);
                s.ErofsCompression = FsForge.Core.Erofs.ErofsCompression.Lz4Hc;
                s.ErofsPclusterSize = 64 * 1024;
                Check(s.ShowErofsLz4Level && !s.ShowErofsLzma && s.ErofsNote.Contains("5.13"), "options of lz4hc: " + s.ErofsNote);
                s.ErofsVolumeName = "a label of more than 16 bytes";
                Check(!s.Accept() && s.Error is not null, "a label that is too long was accepted");
                s.ErofsVolumeName = "vendor";
                return s.Accept();
            };
            string again = Path.Combine(_dir, "vendor.erofs");
            d.SavePicks.Enqueue(again);
            Check(await vm.SaveAsAsync(), "save as EROFS failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Erofs, Erofs: { Compression: FsForge.Core.Erofs.ErofsCompression.Lz4, VolumeName: "vendor", BigPcluster: true } e2 } && e2.Uuid == uuid
                  && vm.DocumentInfo.StartsWith("EROFS · lz4 · кластер "), "saved image: " + vm.DocumentInfo);
            Check(vm.Document!.Tree.Resolve("/etc/passwd") is not null && vm.Document.Tree.Resolve("/dev/null")!.Inode.DevMinor == 3, "content of the saved image");

            // An edit and Ctrl+S: the same file, the same settings.
            vm.Document.Tree.Remove(vm.Document.Tree.Resolve("/etc/passwd")!);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Erofs, FilePath: var path, Erofs.VolumeName: "vendor" } && path == again && vm.Document.Tree.Resolve("/etc/passwd") is null && !vm.IsModified,
                "saved in place: " + vm.DocumentInfo);

            // Converting to ext4 and uncompressed EROFS.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                s.ErofsCompression = FsForge.Core.Erofs.ErofsCompression.None;
                Check(!s.ShowErofsCluster && !s.HasErofsNote && !s.ShowErofsLz4Level, "options without compression");
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "plain.erofs"));
            Check(await vm.SaveAsAsync(), "save as uncompressed EROFS failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Erofs.Compression: FsForge.Core.Erofs.ErofsCompression.None } && vm.DocumentInfo.StartsWith("EROFS · без сжатия"), "uncompressed image: " + vm.DocumentInfo);
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                s.Format = SaveFormat.Ext4;
                Check(s.IsExt && !s.IsErofs && s.Extension == ".ext4", "ext4 after EROFS");
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "from-erofs.ext4"));
            Check(await vm.SaveAsAsync(), "save as ext4 failed: " + string.Join("; ", d.Messages));
            Check(vm.Document!.Format == ImageFormat.Ext && vm.Document.Tree.Resolve("/bin/ash") is not null, "converted image");
            vm.Dispose();
        });

        await Test("cpio: open an initramfs of two archives, keep its settings on saving, change them, convert it", async () =>
        {
            var (vm, d, _, _) = NewVm();
            // As distributions build it: uncompressed CPU microcode, then the system in a gzip-compressed archive.
            string initrd = Path.Combine(_dir, "initrd.img");
            using (ImageDocument source = ImageDocument.Open(MakeImage("cp.sqfs"), _dir))
            using (var early = new FsTree(0x1ED, 1700000000))
            using (var fs = new FileStream(initrd, FileMode.Create))
            {
                FsNode mc = early.CreateDirectory(early.CreateDirectory(early.CreateDirectory(early.Root, "kernel", 0x1ED, 0, 0, 1700000000), "x86", 0x1ED, 0, 0, 1700000000),
                    "microcode", 0x1ED, 0, 0, 1700000000);
                early.CreateFile(mc, "GenuineIntel.bin", new MemoryFileData(new byte[3000]), 0x1A4, 0, 0, 1700000000);
                FsForge.Core.Cpio.CpioWriter.Write(early, fs, new FsForge.Core.Cpio.CpioOptions { Compression = ArchiveCompression.None });
                FsForge.Core.Cpio.CpioWriter.Write(source.Tree, fs, new FsForge.Core.Cpio.CpioOptions { Compression = ArchiveCompression.Gzip, PathPrefix = "./" });
            }
            Check(await vm.OpenPathAsync(initrd), "open failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Cpio, Cpio: { Parts: 2, Compression: ArchiveCompression.Gzip, PathPrefix: "./" } }
                  && vm.DocumentInfo.StartsWith("cpio.gz"), "status line: " + vm.DocumentInfo);
            Check(vm.Document!.Tree.Resolve("/kernel/x86/microcode/GenuineIntel.bin") is not null && vm.Document.Tree.Resolve("/bin/ash")!.Inode.LinkCount == 2,
                "both archives are read into one tree");
            Check(vm.IsInfoOpen && vm.InfoMessage.Contains("2 cpio archives"), "the two archives are not reported: " + vm.InfoMessage);
            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Сжатие" && r.Value == "gzip")
                                                                    && i.Rows.Any(r => r.Label == "Заголовки" && r.Value == "newc")
                                                                    && i.Rows.Any(r => r.Label == "Префикс путей" && r.Value == "./")
                                                                    && i.Rows.Any(r => r.Label == "Архивов подряд" && r.Value == "2");
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of a cpio archive");

            // "Save as" starts with the settings of the source and warns that the microcode archive is merged.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsCpio && s.CpioCompression == ArchiveCompression.Gzip && s.CpioHeader == FsForge.Core.Cpio.CpioHeader.Newc && s.CpioPrefix == "./"
                      && s.CpioRootEntry && s.ShowGzip && !s.ShowXz && s.HasCpioPartsNote && s.CpioPartsNote.Contains("(2)") && s.Extension == ".cpio.gz"
                      && !s.ShowCommonXattrs && s.ShowAllRoot && !s.WritesDisk, "the dialog does not start with the settings of the source");
                s.CpioCompression = ArchiveCompression.Xz;
                Check(s.ShowXz && !s.ShowGzip && s.Extension == ".cpio.xz", "options of xz");
                s.CpioHeader = FsForge.Core.Cpio.CpioHeader.Crc;
                s.CpioPrefix = "";
                s.CpioRootEntry = false;
                return s.Accept();
            };
            string again = Path.Combine(_dir, "initramfs.cpio.xz");
            d.SavePicks.Enqueue(again);
            Check(await vm.SaveAsAsync(), "save as cpio failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Cpio, Cpio: { Parts: 1, Compression: ArchiveCompression.Xz, Header: FsForge.Core.Cpio.CpioHeader.Crc, PathPrefix: "", HasRootEntry: false } }
                  && vm.DocumentInfo.StartsWith("cpio.xz"), "saved archive: " + vm.DocumentInfo);
            Check(vm.Document.Tree.Resolve("/kernel/x86/microcode/GenuineIntel.bin") is not null && vm.Document.Tree.Resolve("/dev/null")!.Inode.DevMinor == 3
                  && vm.Document.Tree.Resolve("/bin/ash")!.Inode.LinkCount == 2, "content of the saved archive");

            // An edit and Ctrl+S: the same file, the same settings.
            vm.Document.Tree.Remove(vm.Document.Tree.Resolve("/kernel")!);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Cpio, FilePath: var path, Cpio: { Header: FsForge.Core.Cpio.CpioHeader.Crc, Compression: ArchiveCompression.Xz } }
                  && path == again && vm.Document.Tree.Resolve("/kernel") is null && !vm.IsModified, "saved in place: " + vm.DocumentInfo);

            // Converting to SquashFS.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsCpio && !s.HasCpioPartsNote, "a single archive needs no note");
                s.Format = SaveFormat.SquashFs;
                Check(s.IsSquash && !s.IsCpio && s.ShowCommonXattrs && s.Extension == ".squashfs", "SquashFS after cpio");
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "from-cpio.squashfs"));
            Check(await vm.SaveAsAsync(), "save as SquashFS failed: " + string.Join("; ", d.Messages));
            Check(vm.Document!.Format == ImageFormat.SquashFs && vm.Document.Tree.Resolve("/bin/ash") is not null, "converted image");
            vm.Dispose();
        });

        await Test("CramFS and romfs: open a big-endian CramFS image, keep its settings, convert it to romfs", async () =>
        {
            var (vm, d, _, _) = NewVm();
            string cramfs = Path.Combine(_dir, "rootfs.cramfs");
            using (ImageDocument source = ImageDocument.Open(MakeImage("cr.sqfs"), _dir))
            using (var fs = new FileStream(cramfs, FileMode.Create))
                ImageDocument.WriteTo(source.Tree, fs, new SaveOptions
                {
                    Format = ImageFormat.Cramfs,
                    Cramfs = new FsForge.Core.Cramfs.CramfsOptions { BigEndian = true, BlockSize = 16384, VolumeName = "router" },
                });
            Check(await vm.OpenPathAsync(cramfs), "open failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Cramfs, Cramfs: { BigEndian: true, BlockSize: 16384 } } && vm.DocumentInfo.StartsWith("CramFS · big-endian · блок 16 КБ"),
                "status line: " + vm.DocumentInfo);
            Check(!vm.IsInfoOpen, "a CramFS image must open without warnings: " + vm.InfoMessage);
            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Порядок байтов" && r.Value == "big-endian")
                                                                    && i.Rows.Any(r => r.Label == "Имя тома" && r.Value == "router")
                                                                    && i.Rows.Any(r => r.Label == "Контрольная сумма" && r.Value == "верна");
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of a CramFS image");

            // "Save as" starts with the settings of the source.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsCramfs && s.CramfsBigEndian && s.CramfsBlockSize == 16384 && s.CramfsVolumeName == "router" && s.CramfsLevel == 9
                      && s.CramfsNote.Contains("big-endian") && s.Extension == ".cramfs" && !s.ShowCommonXattrs && s.ShowAllRoot && !s.ShowNewUbi,
                    "the dialog does not start with the settings of the source");
                Check(s.CramfsBlockSizes.Any(c => c.Value == s.CramfsBlockSize), "the block size is not in the list");
                s.CramfsVolumeName = "a name longer than 16 bytes";
                Check(!s.Accept() && s.Error is not null, "a name that is too long was accepted");
                s.CramfsVolumeName = "rootfs";
                s.CramfsBigEndian = false;
                s.CramfsBlockSize = 4096;
                s.CramfsHoles = true;
                Check(s.CramfsNote.Contains("little-endian"), "note: " + s.CramfsNote);
                return s.Accept();
            };
            string again = Path.Combine(_dir, "rootfs-le.cramfs");
            d.SavePicks.Enqueue(again);
            Check(await vm.SaveAsAsync(), "save as CramFS failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Cramfs, Cramfs: { BigEndian: false, BlockSize: 4096, Holes: true, VolumeName: "rootfs" } }
                  && vm.DocumentInfo.StartsWith("CramFS · little-endian · блок 4 КБ"), "saved image: " + vm.DocumentInfo);
            Check(vm.Document!.Tree.Resolve("/etc/passwd") is not null && vm.Document.Tree.Resolve("/dev/null")!.Inode.DevMinor == 3, "content of the saved image");

            // An edit and Ctrl+S: the same file, the same settings.
            vm.Document.Tree.Remove(vm.Document.Tree.Resolve("/etc/passwd")!);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Cramfs, FilePath: var path, Cramfs.Holes: true } && path == again && vm.Document.Tree.Resolve("/etc/passwd") is null
                  && !vm.IsModified, "saved in place: " + vm.DocumentInfo);

            // Converting to romfs: no owners, so no "all root" switch.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                s.Format = SaveFormat.Romfs;
                Check(s.IsRomfs && !s.IsCramfs && !s.ShowAllRoot && !s.ShowCommonXattrs && s.Extension == ".romfs" && s.RomfsVolumeName == "rom", "romfs after CramFS");
                s.RomfsVolumeName = "flash";
                s.RomfsAlignment = 512;
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "flash.romfs"));
            Check(await vm.SaveAsAsync(), "save as romfs failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Romfs, Romfs: { VolumeName: "flash", DataAlignment: 512 } } && vm.DocumentInfo.StartsWith("romfs · flash"),
                "romfs image: " + vm.DocumentInfo);
            // CramFS has no hard links: after it, /bin/ash is a file of its own with the same content.
            Check(vm.Document.Tree.Resolve("/bin/ash")!.Inode is { LinkCount: 1, Size: 50000 }, "the former hard link");
            infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Выравнивание данных" && r.Value == "512 Б")
                                                                    && i.Rows.Any(r => r.Label == "Контрольная сумма" && r.Value == "верна");
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of a romfs image");
            vm.Dispose();
        });

        await Test("Archives: a FlashTool 7z with two images and a README, checksum file, temporary folder", async () =>
        {
            var (vm, d, _, _) = NewVm();
            byte[] image = File.ReadAllBytes(MakeImage("arc.sqfs"));
            byte[] readme = Encoding.ASCII.GetBytes("Flash me\n");
            string archive = Path.Combine(_dir, "firmware.7z");
            using (var fs = new FileStream(archive, FileMode.Create, FileAccess.ReadWrite))
                FsForge.Core.Archives.SevenZipWriter.Write(fs, new[]
                {
                    new FsForge.Core.Archives.ArchiveWriteEntry { Name = "README.txt", Length = readme.Length, WriteData = s => s.Write(readme) },
                    new FsForge.Core.Archives.ArchiveWriteEntry { Name = "rootfs.img", Length = image.Length, WriteData = s => s.Write(image) },
                    new FsForge.Core.Archives.ArchiveWriteEntry { Name = "recovery.img", Length = image.Length, WriteData = s => s.Write(image) },
                }, 0, 0);
            string sidecar = archive + ".sha256";
            File.WriteAllText(sidecar, "0000000000000000000000000000000000000000000000000000000000000000  firmware.7z\n");

            // Two images: the user picks one; the README is listed but is not an image.
            int asked = 0;
            d.OnDialog = o =>
            {
                var c = (PartitionChoiceViewModel)o;
                asked++;
                Check(c.Rows.Count == 3 && c.Rows[0] is { Name: "README.txt", Content: "файл" } && c.Rows[2] is { Name: "recovery.img", Content: "образ", IsEditable: true }
                      && c.Summary.Contains("7z") && c.Title == "Выбор файла в архиве", "archive list: " + string.Join("; ", c.Rows.Select(r => r.Name + " " + r.Content)));
                c.Selected = c.Rows[2];
                return c.Accept();
            };
            Check(await vm.OpenPathAsync(archive), "open failed: " + string.Join("; ", d.Messages));
            Check(asked == 1 && vm.Document is { Archive: { EntryName: "recovery.img", Kind: ArchiveCompression.SevenZip } }
                  && vm.DocumentInfo.StartsWith("recovery.img в архиве 7z · SquashFS"), "status line: " + vm.DocumentInfo);
            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Архив" && r.Value.StartsWith("7z (LZMA2)"))
                                                                    && i.Rows.Any(r => r.Label == "Другие файлы архива" && r.Value.StartsWith("README.txt, rootfs.img"));
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of an image in an archive");

            // Ctrl+S: the same archive, the other files kept, the checksum file updated.
            vm.Document!.Tree.Remove(vm.Document.Tree.Resolve("/etc/passwd")!);
            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog on save: " + o.GetType().Name);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Archive.EntryName: "recovery.img", FilePath: var path } && path == archive && vm.Document.Tree.Resolve("/etc/passwd") is null,
                "saved in place: " + vm.DocumentInfo);
            using (var a = FsForge.Core.Archives.ImageArchive.Open(archive, ArchiveCompression.SevenZip))
                Check(string.Join(",", a.Entries.Select(e => e.Name)) == "README.txt,rootfs.img,recovery.img", "entries after saving");
            string hash;
            using (var f = File.OpenRead(archive)) hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f)).ToLowerInvariant();
            Check(File.ReadAllText(sidecar) == hash + "  firmware.7z\n", "the checksum file was not updated");
            Check(d.Notifications.Any(n => n.Contains("обновлены файлы рядом с ним: firmware.7z.sha256")) && !vm.IsInfoOpen,
                "the user was not told about the checksum file: " + string.Join("; ", d.Notifications));

            // The settings show where temporary files go and how much room there is.
            var settings = new SettingsViewModel(vm.Settings.Clone(), d, Path.Combine(_dir, "s.json"));
            Check(settings.TempSpaceText.StartsWith("Сейчас: ") && settings.TempSpaceText.Contains("свободно"), "temporary folder hint: " + settings.TempSpaceText);
            vm.Dispose();
        });

        await Test("exFAT: open a labelled image, keep its settings, save in place, convert to FAT", async () =>
        {
            var (vm, d, _, _) = NewVm();
            string exfat = Path.Combine(_dir, "card.exfat");
            using (ImageDocument source = ImageDocument.Open(MakeImage("ex.sqfs"), _dir))
            using (var fs = new FileStream(exfat, FileMode.Create))
                ImageDocument.WriteTo(source.Tree, fs, new SaveOptions
                {
                    Format = ImageFormat.ExFat,
                    ExFat = new FsForge.Core.ExFat.ExFatOptions { SizeBytes = 64L << 20, ClusterSize = 32768, Label = "CAMERA", VolumeSerial = 0x1234ABCD },
                });
            Check(await vm.OpenPathAsync(exfat), "open failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.ExFat, ExFat: { Label: "CAMERA", ClusterSize: 32768 } } && vm.DocumentInfo.StartsWith("exFAT · 64 МБ · кластер 32 КБ · свободно"),
                "status line: " + vm.DocumentInfo);
            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Метка" && r.Value == "CAMERA")
                                                                    && i.Rows.Any(r => r.Label == "Серийный номер" && r.Value == "1234-ABCD")
                                                                    && i.Rows.Any(r => r.Label == "Состояние" && r.Value == "чистая");
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of an exFAT image");

            // "Save as" starts with the settings of the source.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsExFat && s.ExFatSizeMiB == "64" && s.ExFatClusterSize == 32768 && s.ExFatLabel == "CAMERA" && s.ExFatSizeEditable && s.Extension == ".exfat"
                      && !s.ShowCommonXattrs && !s.ShowAllRoot && s.ExFatClusterSizes.Any(c => c.Value == 32768), "the dialog does not start with the settings of the source");
                s.ExFatLabel = "twelve chars";
                Check(!s.Accept() && s.Error is not null, "a label that is too long was accepted");
                s.ExFatLabel = "Фото";
                s.ExFatSizeMiB = "";
                s.ExFatFreeMiB = "2";
                s.ExFatClusterSize = 4096;
                return s.Accept();
            };
            string again = Path.Combine(_dir, "card-auto.exfat");
            d.SavePicks.Enqueue(again);
            Check(await vm.SaveAsAsync(), "save as exFAT failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.ExFat, ExFat: { Label: "Фото", ClusterSize: 4096, VolumeSerial: 0x1234ABCD } } && vm.Document.ExFat.SizeBytes < 64L << 20,
                "saved image: " + vm.DocumentInfo);

            // An edit and Ctrl+S: the same file and settings.
            vm.Document!.Tree.Remove(vm.Document.Tree.Resolve("/etc/passwd")!);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.ExFat, FilePath: var path, ExFat.Label: "Фото" } && path == again && vm.Document.Tree.Resolve("/etc/passwd") is null && !vm.IsModified,
                "saved in place: " + vm.DocumentInfo);

            // Converting to FAT: the same kind of content.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                s.Format = SaveFormat.Fat;
                Check(s.IsFat && !s.IsExFat && s.Extension == ".vfat", "FAT after exFAT");
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "from-exfat.vfat"));
            Check(await vm.SaveAsAsync(), "save as FAT failed: " + string.Join("; ", d.Messages));
            Check(vm.Document!.Format == ImageFormat.Fat && vm.Document.Tree.Resolve("/bin/busybox") is not null, "converted image");
            vm.Dispose();
        });

        await Test("F2FS: open a labelled image, keep its size and name, save in place, convert", async () =>
        {
            var (vm, d, _, _) = NewVm();
            string f2fs = Path.Combine(_dir, "userdata.f2fs");
            using (ImageDocument source = ImageDocument.Open(MakeImage("f2.sqfs"), _dir))
            using (var fs = new FileStream(f2fs, FileMode.Create))
                ImageDocument.WriteTo(source.Tree, fs, new SaveOptions
                {
                    Format = ImageFormat.F2fs,
                    F2fs = new FsForge.Core.F2fs.F2fsOptions { SizeBytes = 128L << 20, VolumeName = "data" },
                });
            Check(await vm.OpenPathAsync(f2fs), "open failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.F2fs, F2fs: { VolumeName: "data", BlockSize: 4096 } } && vm.DocumentInfo.StartsWith("F2FS · 128 МБ · свободно"),
                "status line: " + vm.DocumentInfo);
            Check(!vm.IsInfoOpen, "an F2FS image must open without warnings: " + vm.InfoMessage);
            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Имя тома" && r.Value == "data")
                                                                    && i.Rows.Any(r => r.Label == "Состояние" && r.Value == "отмонтирована штатно")
                                                                    && i.Rows.Any(r => r.Label == "Версия формата" && r.Value.Contains("."));
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of an F2FS image");

            // "Save as" starts with the size and the name of the source.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsF2fs && s.F2fsSizeMiB == "128" && s.F2fsVolumeName == "data" && s.F2fsSizeEditable && s.Extension == ".f2fs"
                      && s.ShowCommonXattrs && s.ShowAllRoot && !s.ShowNewUbi, "the dialog does not start with the settings of the source");
                s.F2fsSizeMiB = "many";
                Check(!s.Accept() && s.Error is not null, "a size that is not a number was accepted");
                s.F2fsSizeMiB = "";
                s.F2fsFreeMiB = "-1";
                Check(!s.Accept() && s.Error is not null, "a negative reserve was accepted");
                s.F2fsFreeMiB = "4";
                s.F2fsVolumeName = new string('x', 513);
                Check(!s.Accept() && s.Error is not null, "a name that is too long was accepted");
                s.F2fsVolumeName = "userdata";
                return s.Accept();
            };
            string again = Path.Combine(_dir, "userdata-auto.f2fs");
            d.SavePicks.Enqueue(again);
            Check(await vm.SaveAsAsync(), "save as F2FS failed: " + string.Join("; ", d.Messages));
            long size = vm.Document!.F2fs!.BlockCount * 4096;
            Check(vm.Document is { Format: ImageFormat.F2fs, F2fs.VolumeName: "userdata" } && size < 128L << 20 && new FileInfo(again).Length == size,
                "saved image: " + vm.DocumentInfo);
            Check(vm.Document.Tree.Resolve("/etc/passwd") is not null && vm.Document.Tree.Resolve("/dev/null")!.Inode.DevMinor == 3, "content of the saved image");

            // An edit and Ctrl+S: the same file, the same size and name.
            vm.Document.Tree.Remove(vm.Document.Tree.Resolve("/etc/passwd")!);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.F2fs, FilePath: var path, F2fs.VolumeName: "userdata" } && path == again
                  && vm.Document.F2fs.BlockCount * 4096 == size && vm.Document.Tree.Resolve("/etc/passwd") is null && !vm.IsModified,
                "saved in place: " + vm.DocumentInfo);

            // Converting to ext4.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                s.Format = SaveFormat.Ext4;
                Check(s.IsExt && !s.IsF2fs && s.Extension == ".ext4", "ext4 after F2FS");
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "from-f2fs.ext4"));
            Check(await vm.SaveAsAsync(), "save as ext4 failed: " + string.Join("; ", d.Messages));
            Check(vm.Document!.Format == ImageFormat.Ext && vm.Document.Tree.Resolve("/bin/ash") is not null, "converted image");
            vm.Dispose();
        });

        await Test("switching partitions without closing the file: disk image, super.img", async () =>
        {
            var (vm, d, _, _) = NewVm();
            string disk = MakeDisk("switch.img.gz", out long rootOffset);
            int TempFiles() => Directory.GetFiles(_dir, "fsforge-*.img").Length;
            int tempBefore = TempFiles();

            // A file with one partition has nothing to switch to.
            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog: " + o.GetType().Name);
            Check(await vm.OpenPathAsync(MakeImage("plain-switch.sqfs")), "open plain image");
            Check(!vm.HasParts && vm.Parts.Count == 0 && vm.CurrentPart is null, "a plain image lists parts");

            d.OnDialog = o => o is PartitionChoiceViewModel c && (c.Selected = c.Rows[1]) is not null && c.Accept();
            Check(await vm.OpenPathAsync(disk), "open failed: " + string.Join("; ", d.Messages));
            Check(vm.HasParts && vm.Parts.Count == 2 && vm.CurrentPart is { Part.PartitionNumber: 2, Text: "Раздел 2 · SquashFS" }, "parts: " + string.Join("; ", vm.Parts.Select(p => p.Text)));
            Check(vm.Parts[0] is { Text: "Раздел 1 · ext2 «boot»", CanOpen: true, IsCurrent: false } && vm.Parts[0].Details.Contains("смещение 0x100000"), "boot row: " + vm.Parts[0].Text + " / " + vm.Parts[0].Details);
            Check(!vm.SwitchPartCommand.CanExecute(vm.Parts[1]) && vm.SwitchPartCommand.CanExecute(vm.Parts[0]), "switch command state");
            int tempOpen = TempFiles();
            Check(tempOpen == tempBefore + 1, "the gzipped disk is unpacked once: " + (tempOpen - tempBefore));

            // Switch to the boot partition: no dialog, no second unpacking, the status line follows.
            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog on switch: " + o.GetType().Name);
            vm.CurrentPath = "/etc";
            Check(await vm.SwitchPartAsync(vm.Parts[0]), "switch failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Ext, Container.Partition.Number: 1, FilePath: not null } && vm.Document.FilePath.EndsWith("switch.img.gz"), "partition 1 is not open");
            Check(vm.CurrentPath == "/" && vm.Items.Any(i => i.Name == "uImage") && vm.CurrentPart is { Part.PartitionNumber: 1 }, "view after the switch: " + vm.CurrentPath);
            Check(vm.DocumentInfo.StartsWith("образ диска MBR · раздел 1 из 2 · ext2"), "status line: " + vm.DocumentInfo);
            Check(TempFiles() == tempOpen, "switching unpacked the file again");

            // Unsaved changes: "Cancel" stays, "Save" writes the whole disk and then switches.
            Select(vm, "uImage");
            d.Answers.Enqueue(DialogAnswer.Primary);
            await vm.DeleteCommand.ExecuteAsync();
            Check(vm.IsModified, "the deletion did not modify the image");
            d.Answers.Enqueue(DialogAnswer.Cancel);
            Check(!await vm.SwitchPartAsync(vm.Parts[1]) && vm.Document!.Container!.Partition.Number == 1 && vm.IsModified, "cancel switched anyway");
            Check(d.Asked[^1] == "Несохранённые изменения", "not asked about the changes: " + d.Asked[^1]);
            d.Answers.Enqueue(DialogAnswer.Primary);
            Check(await vm.SwitchPartAsync(vm.Parts[1]), "save and switch failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.SquashFs, Container.Partition.Number: 2, IsModified: false } && vm.Items.Any(i => i.Name == "etc"), "partition 2 after saving");
            Check(vm.Document!.OuterCompression == ArchiveCompression.Gzip && Gunzip(disk).Length >= rootOffset, "the disk is still gzipped");
            Check(await vm.SwitchPartAsync(vm.Parts[0]) && !vm.Items.Any(i => i.Name == "uImage"), "the saved change of partition 1 is missing");

            // The switcher setter goes the same way; a part that cannot be opened is refused.
            vm.CurrentPart = vm.Parts[1];
            for (int i = 0; i < 200 && (vm.IsBusy || vm.Document!.Container!.Partition.Number != 2); i++) await Task.Delay(20);
            Check(vm.Document!.Container!.Partition.Number == 2 && !vm.IsBusy, "the switcher did not open partition 2");

            // Both documents share the unpacked file; the last one closes (and deletes) it.
            using (ImageDocument first = ImageDocument.Open(disk, _dir, partition: 1))
            {
                Check(first.Parts.Count == 2 && first.Parts[0].IsCurrent && first.CanOpenOtherPart, "parts of a disk document");
                ImageDocument second = first.OpenPart(first.Parts[1], _dir);
                first.Dispose();
                FsNode file = second.Tree.Resolve("/etc/passwd") ?? throw new InvalidOperationException("no /etc/passwd in partition 2");
                using (var reader = new StreamReader(file.Inode.Data!.OpenRead()))
                    Check(reader.ReadToEnd().StartsWith("root:x:0:0"), "partition 2 is unreadable after the first document was closed");
                Check(second.Parts[1].IsCurrent, "the second document marks partition 2");
                second.Dispose();
            }
            vm.Dispose();
            Check(TempFiles() == tempBefore, "the unpacked disk was not deleted: " + (TempFiles() - tempBefore));

            // super.img: an empty logical partition is listed but cannot be opened.
            (vm, d, _, _) = NewVm();
            byte[] system;
            using (ImageDocument source = ImageDocument.Open(MakeImage("sw.sqfs"), _dir))
            {
                var ms = new MemoryStream();
                ImageDocument.WriteTo(source.Tree, ms, new SaveOptions { Format = ImageFormat.Ext, Ext = new FsForge.Core.Ext.ExtOptions { SizeBytes = 4 << 20, Journal = false } });
                system = ms.ToArray();
            }
            byte[] vendor = File.ReadAllBytes(MakeImage("sw-vendor.sqfs"));
            Array.Resize(ref vendor, (vendor.Length + 4095) / 4096 * 4096);
            string super = Path.Combine(_dir, "switch-super.img");
            File.WriteAllBytes(super, BuildSuper(32 << 20, new[] { ("system_a", 2048L, system), ("vendor_a", 16L << 11, vendor), ("system_b", 0L, Array.Empty<byte>()) }));
            d.OnDialog = o => o is PartitionChoiceViewModel c && c.Accept();
            Check(await vm.OpenPathAsync(super), "open super failed: " + string.Join("; ", d.Messages));
            Check(vm.Parts.Select(p => p.Text).SequenceEqual(new[] { "system_a · ext4", "vendor_a · SquashFS", "system_b · пусто" }), "super parts: " + string.Join("; ", vm.Parts.Select(p => p.Text)));
            Check(vm.CurrentPart?.Name == "system_a" && !vm.Parts[2].CanOpen && !vm.SwitchPartCommand.CanExecute(vm.Parts[2]), "empty partition state");
            Check(!await vm.SwitchPartAsync(vm.Parts[2]) && vm.Document!.Android!.Part.Name == "system_a", "an empty partition was opened");
            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog on switch: " + o.GetType().Name);
            Check(await vm.SwitchPartAsync(vm.Parts[1]) && vm.Document is { Format: ImageFormat.SquashFs, Android.Part.Name: "vendor_a" }, "switch to vendor_a: " + string.Join("; ", d.Messages));
            Check(vm.DocumentInfo.StartsWith("super.img · vendor_a из 3 · SquashFS"), "status line: " + vm.DocumentInfo);
            vm.Dispose();
        });

        await Test("Android: super.img with two partitions, a boot image with an lz4 ramdisk", async () =>
        {
            var (vm, d, _, _) = NewVm();
            // super.img of 32 MiB as liblp lays it out: system_a (ext4) at 1 MiB, vendor_a (SquashFS) at 16 MiB, an empty system_b.
            byte[] system, vendor;
            using (ImageDocument source = ImageDocument.Open(MakeImage("an.sqfs"), _dir))
            {
                var ms = new MemoryStream();
                ImageDocument.WriteTo(source.Tree, ms, new SaveOptions { Format = ImageFormat.Ext, Ext = new FsForge.Core.Ext.ExtOptions { SizeBytes = 4 << 20, Journal = false } });
                system = ms.ToArray();
                ms = new MemoryStream();
                ImageDocument.WriteTo(source.Tree, ms, new SaveOptions { Format = ImageFormat.SquashFs });
                vendor = ms.ToArray();
                Array.Resize(ref vendor, (vendor.Length + 4095) / 4096 * 4096);
            }
            string super = Path.Combine(_dir, "super.img");
            File.WriteAllBytes(super, BuildSuper(32 << 20, new[] { ("system_a", 2048L, system), ("vendor_a", 16L << 11, vendor), ("system_b", 0L, Array.Empty<byte>()) }));

            int asked = 0;
            d.OnDialog = o =>
            {
                var c = (PartitionChoiceViewModel)o;
                asked++;
                Check(c.Title == "Выбор раздела" && c.Summary.Contains("super.img с логическими разделами (разделов: 3)") && c.Rows.Count == 3, "dialog: " + c.Summary);
                Check(c.Selected is { Name: "system_a", Content: "ext4", IsEditable: true } && c.Rows[2] is { Name: "system_b", Content: "пусто", IsEditable: false },
                    "rows: " + string.Join("; ", c.Rows.Select(r => r.Name + " " + r.Content)));
                c.Selected = c.Rows[2];
                Check(!c.Accept() && c.Error!.Contains("нет файловой системы"), "an empty partition was accepted");
                c.Selected = c.Rows[1];
                return c.Accept();
            };
            Check(await vm.OpenPathAsync(super), "open failed: " + string.Join("; ", d.Messages));
            Check(asked == 1 && vm.Document is { Format: ImageFormat.SquashFs, Android.Part.Name: "vendor_a" } && vm.DocumentInfo.StartsWith("super.img · vendor_a из 3 · SquashFS"),
                "status line: " + vm.DocumentInfo);
            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Образ Android" && r.Value.StartsWith("super.img"))
                                                                    && i.Rows.Any(r => r.Label == "▸ vendor_a (открыт)" && r.Value.StartsWith("SquashFS"))
                                                                    && i.Rows.Any(r => r.Label == "system_a" && r.Value.StartsWith("ext4"));
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of a super image");

            // "Save as": the whole super.img by default, under its own name.
            vm.Document!.Tree.Remove(vm.Document.Tree.Resolve("/etc/Banner")!);
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.HasContainer && s.IsAndroidContainer && s.WritesDisk && s.ContainerTitle == "Образ Android" && s.ContainerNote.Contains("super.img записывается заново")
                      && s.ContainerSummary.Contains("Открыт раздел vendor_a") && s.Extension == ".img" && !s.ShowNewUbi, "save dialog: " + s.ContainerNote);
                s.Format = SaveFormat.Ubifs;
                Check(!s.WritesDisk && s.ContainerNote.Contains("отдельным файлом"), "UBIFS into a logical partition: " + s.ContainerNote);
                s.Format = SaveFormat.SquashFs;
                return s.Accept();
            };
            d.SavePicks.Enqueue(super);
            Check(await vm.SaveAsAsync(), "save failed: " + string.Join("; ", d.Messages));
            Check(d.LastSuggestedName == "super.img" && d.LastSaveWasDisk, "suggested name: " + d.LastSuggestedName);
            Check(vm.Document is { Android.Part.Name: "vendor_a" } && vm.Document.Tree.Resolve("/etc/Banner") is null && !vm.IsModified, "saved super image");
            using (ImageDocument sys = ImageDocument.Open(super, _dir, volume: "system_a"))
                Check(sys.Tree.Resolve("/etc/Banner") is not null, "system_a changed");

            // The partition alone, for fastboot.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                s.KeepContainer = false;
                Check(!s.WritesDisk && s.ContainerNote.Contains("fastboot") && s.Extension == ".squashfs", "the partition alone: " + s.ContainerNote);
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "vendor_a.squashfs"));
            Check(await vm.SaveAsAsync(), "save alone failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Android: null, Format: ImageFormat.SquashFs }, "the partition saved alone");

            // boot.img v3 with an lz4 ramdisk: no choice to make; Ctrl+S writes the boot image again.
            byte[] ramdisk;
            using (ImageDocument source = ImageDocument.Open(MakeImage("an2.sqfs"), _dir))
            {
                var ms = new MemoryStream();
                ImageDocument.WriteTo(source.Tree, ms, new SaveOptions { Format = ImageFormat.Cpio, Cpio = new FsForge.Core.Cpio.CpioOptions { Compression = ArchiveCompression.Lz4 } });
                ramdisk = ms.ToArray();
            }
            string boot = Path.Combine(_dir, "boot.img");
            File.WriteAllBytes(boot, BuildBootV3(new byte[12345], ramdisk));
            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog: " + o.GetType().Name);
            Check(await vm.OpenPathAsync(boot), "open boot failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Cpio, Cpio.Compression: ArchiveCompression.Lz4, Android.Part.Name: "ramdisk" } && vm.DocumentInfo.StartsWith("boot · ramdisk ramdisk · cpio.lz4"),
                "boot status line: " + vm.DocumentInfo);
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsCpio && s.CpioCompression == ArchiveCompression.Lz4 && s.WritesDisk && s.ContainerNote.Contains("Загрузочный образ записывается заново"), "boot dialog: " + s.ContainerNote);
                s.Format = SaveFormat.SquashFs;
                Check(!s.WritesDisk && s.ContainerNote.Contains("архив cpio"), "another format: " + s.ContainerNote);
                s.Format = SaveFormat.Cpio;
                return s.Accept();
            };
            vm.Document!.Tree.CreateFile(vm.Document.Tree.Root, "init.rc", new MemoryFileData(Encoding.ASCII.GetBytes("on boot\n")));
            d.SavePicks.Enqueue(boot);
            Check(await vm.SaveAsAsync(), "save boot failed: " + string.Join("; ", d.Messages));
            byte[] written = File.ReadAllBytes(boot);
            Check(Encoding.ASCII.GetString(written, 0, 8) == "ANDROID!" && vm.Document is { Android.Part.Name: "ramdisk" } && vm.Document.Tree.Resolve("/init.rc") is not null,
                "the boot image was not written again");
            vm.Dispose();
        });

        await Test("U-Boot uImage: choice of part, information, rebuilt on save, filesystem saved alone", async () =>
        {
            var (vm, d, _, _) = NewVm();
            // A multi-file uImage: a kernel, an uncompressed cpio ramdisk and a SquashFS, each padded to 4 bytes.
            byte[] squash = File.ReadAllBytes(MakeImage("ui.sqfs")), cpio;
            using (ImageDocument source = ImageDocument.Open(MakeImage("ui-src.sqfs"), _dir))
            {
                var ms = new MemoryStream();
                ImageDocument.WriteTo(source.Tree, ms, new SaveOptions { Format = ImageFormat.Cpio, Cpio = new FsForge.Core.Cpio.CpioOptions { Compression = ArchiveCompression.None } });
                cpio = ms.ToArray();
            }
            byte[][] images = { new byte[10001], cpio, squash };
            var body = new MemoryStream();
            foreach (byte[] i in images.Append(Array.Empty<byte>())) body.Write(BigEndian((uint)i.Length));
            foreach (byte[] i in images) { body.Write(i); body.Write(new byte[(4 - i.Length % 4) % 4]); }
            byte[] data = body.ToArray();
            byte[] header = new byte[64];
            BigEndian(0x27051956).CopyTo(header, 0);
            BigEndian((uint)data.Length).CopyTo(header, 12);
            BigEndian(FsForge.Core.Compression.Crc32.Compute(data)).CopyTo(header, 24);
            (header[28], header[29], header[30], header[31]) = (5, 2, 4, 0);       // Linux, ARM, multi, uncompressed
            Encoding.ASCII.GetBytes("board firmware").CopyTo(header, 32);
            BigEndian(FsForge.Core.Compression.Crc32.Compute(header)).CopyTo(header, 4);
            string fw = Path.Combine(_dir, "board.uImage");
            File.WriteAllBytes(fw, header.Concat(data).ToArray());
            byte[] original = File.ReadAllBytes(fw);

            int asked = 0;
            d.OnDialog = o =>
            {
                var c = (PartitionChoiceViewModel)o;
                asked++;
                Check(c.Title == "Выбор части прошивки" && c.Summary.Contains("U-Boot uImage (multi-file) «board firmware»") && c.Rows.Count == 3, "dialog: " + c.Summary);
                Check(c.Rows[0] is { Name: "kernel", IsEditable: false } && c.Rows[1] is { Name: "ramdisk", Content: "cpio" } && c.Rows[2] is { Name: "rootfs", Content: "SquashFS" },
                    "rows: " + string.Join("; ", c.Rows.Select(r => r.Name + " " + r.Content)));
                c.Selected = c.Rows[0];
                Check(!c.Accept() && c.Error!.Contains("нет файловой системы"), "the kernel was accepted");
                c.Selected = c.Rows[2];
                return c.Accept();
            };
            Check(await vm.OpenPathAsync(fw), "open failed: " + string.Join("; ", d.Messages));
            Check(asked == 1 && vm.Document is { Format: ImageFormat.SquashFs, Firmware.Part.Name: "rootfs", SavesFilesystemOnly: false }
                  && vm.DocumentInfo.StartsWith("uImage · rootfs из 3 · SquashFS"), "status line: " + vm.DocumentInfo);
            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Прошивка" && r.Value.Contains("board firmware"))
                                                                    && i.Rows.Any(r => r.Label == "Заголовок uImage" && r.Value.StartsWith("Linux ARM, тип multi"))
                                                                    && i.Rows.Any(r => r.Label == "Контрольная сумма данных" && r.Value == "совпадает")
                                                                    && i.Rows.Any(r => r.Label == "▸ rootfs (открыт)" && r.Value.StartsWith("SquashFS"))
                                                                    && i.Rows.Any(r => r.Label == "ramdisk" && r.Value.StartsWith("cpio"));
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of a uImage");

            // Ctrl+S writes the firmware again: the kernel and the ramdisk stay, the header gets new sizes and checksums.
            vm.Document!.Tree.Remove(vm.Document.Tree.Resolve("/etc/Banner")!);
            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog: " + o.GetType().Name);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            byte[] rebuilt = File.ReadAllBytes(fw);
            uint size = (uint)(rebuilt[12] << 24 | rebuilt[13] << 16 | rebuilt[14] << 8 | rebuilt[15]);
            byte[] head = rebuilt[..64];
            uint hcrc = (uint)(head[4] << 24 | head[5] << 16 | head[6] << 8 | head[7]);
            head[4] = head[5] = head[6] = head[7] = 0;
            Check(FsForge.Core.Compression.Crc32.Compute(head) == hcrc && size == rebuilt.Length - 64
                  && rebuilt.AsSpan(64 + 16, 10001).SequenceEqual(original.AsSpan(64 + 16, 10001)) && !rebuilt.AsSpan().SequenceEqual(original), "the firmware was not rebuilt");
            Check(vm.Document is { Firmware.Part.Name: "rootfs" } && vm.Document.Tree.Resolve("/etc/Banner") is null && !vm.IsModified, "the rebuilt firmware reopened");

            // "Save as" offers the whole firmware or the filesystem alone.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.HasContainer && s.IsFirmwareContainer && s.WritesDisk && s.ContainerTitle == "Прошивка" && s.ContainerSummary.Contains("Открыта часть rootfs")
                      && s.ContainerNote.Contains("Размер и контрольные суммы"), "save dialog of a firmware part: " + s.ContainerNote);
                s.Format = SaveFormat.Tar;
                Check(!s.WritesDisk && s.ContainerNote.Contains("отдельным файлом"), "tar into the firmware: " + s.ContainerNote);
                s.Format = SaveFormat.SquashFs;
                s.KeepContainer = false;
                Check(!s.WritesDisk && s.ContainerNote.Contains("только файловая система"), "the filesystem alone: " + s.ContainerNote);
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "board-rootfs.squashfs"));
            Check(await vm.SaveAsAsync(), "save as failed: " + string.Join("; ", d.Messages));
            Check(!d.LastSaveWasDisk && File.ReadAllBytes(fw).AsSpan().SequenceEqual(rebuilt), "the firmware file was changed by saving the filesystem alone");
            Check(vm.Document is { Firmware: null, Format: ImageFormat.SquashFs } && vm.Document.Tree.Resolve("/etc/passwd") is not null, "the filesystem saved alone");
            vm.Dispose();
        });

        await Test("FIT image: choice of image, information, rebuilt on save, filesystem saved alone", async () =>
        {
            var (vm, d, _, _) = NewVm();
            byte[] squash = File.ReadAllBytes(MakeImage("fit.sqfs")), cpio;
            using (ImageDocument source = ImageDocument.Open(MakeImage("fit-src.sqfs"), _dir))
            {
                var ms = new MemoryStream();
                ImageDocument.WriteTo(source.Tree, ms, new SaveOptions { Format = ImageFormat.Cpio, Cpio = new FsForge.Core.Cpio.CpioOptions { Compression = ArchiveCompression.Gzip } });
                cpio = ms.ToArray();
            }
            string fit = Path.Combine(_dir, "board.itb");
            File.WriteAllBytes(fit, BuildFit(("ramdisk-1", "ramdisk", cpio), ("rootfs-1", "filesystem", squash)));
            byte[] original = File.ReadAllBytes(fit);

            d.OnDialog = o =>
            {
                var c = (PartitionChoiceViewModel)o;
                Check(c.Title == "Выбор части прошивки" && c.Summary.Contains("U-Boot FIT image «board FIT»") && c.Rows.Count == 2
                      && c.Rows[1] is { Name: "rootfs-1", Content: "SquashFS", IsEditable: true }, "dialog: " + c.Summary);
                c.Selected = c.Rows[1];
                return c.Accept();
            };
            Check(await vm.OpenPathAsync(fit), "open failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.SquashFs, Firmware.Part.Name: "rootfs-1", SavesFilesystemOnly: false }
                  && vm.DocumentInfo.StartsWith("FIT · rootfs-1 из 2 · SquashFS"), "status line: " + vm.DocumentInfo);
            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Прошивка" && r.Value.Contains("FIT image «board FIT»"))
                                                                    && i.Rows.Any(r => r.Label == "Конфигурация по умолчанию" && r.Value.StartsWith("conf-1"))
                                                                    && i.Rows.Any(r => r.Label == "Хеши образов" && r.Value == "проверено 2, все совпадают")
                                                                    && i.Rows.Any(r => r.Label == "▸ rootfs-1 (открыт)" && r.Value.StartsWith("SquashFS"));
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of a FIT image");

            // Ctrl+S writes the FIT image again: the other image stays, the hashes are right.
            vm.Document!.Tree.Remove(vm.Document.Tree.Resolve("/etc/Banner")!);
            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog: " + o.GetType().Name);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            byte[] rebuilt = File.ReadAllBytes(fit);
            Check(!rebuilt.AsSpan().SequenceEqual(original) && rebuilt.AsSpan().IndexOf(cpio) > 0, "the FIT image was not rebuilt, or the ramdisk changed");
            Check(vm.Document is { Firmware: { Part.Name: "rootfs-1", Image: FsForge.Core.Firmware.FitImage { HashesWrong: 0, HashesChecked: 2 } } } && vm.Document.Tree.Resolve("/etc/Banner") is null,
                "the rebuilt FIT image reopened");

            // "Save as" can still write the filesystem alone, and then the FIT image stays as it is.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsFirmwareContainer && s.WritesDisk && s.ContainerNote.Contains("дереве FIT пересчитываются"), "save dialog of a FIT image: " + s.ContainerNote);
                s.KeepContainer = false;
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "board-rootfs.squashfs"));
            Check(await vm.SaveAsAsync(), "save as failed: " + string.Join("; ", d.Messages));
            Check(File.ReadAllBytes(fit).AsSpan().SequenceEqual(rebuilt) && vm.Document is { Firmware: null }, "the filesystem saved alone, the FIT image unchanged");
            vm.Dispose();
        });

        await Test("TRX image: information, rebuilt on save, filesystem saved alone", async () =>
        {
            var (vm, d, _, _) = NewVm();
            byte[] squash = File.ReadAllBytes(MakeImage("trx.sqfs"));
            // A TRX image (version 1): header, a kernel at 28, the SquashFS at 64 KiB; the CRC from the flags to the end.
            byte[] trx = new byte[65536 + squash.Length];
            new Random(5).NextBytes(trx.AsSpan(28, 30000));
            squash.CopyTo(trx, 65536);
            BitConverter.GetBytes(0x30524448u).CopyTo(trx, 0);
            BitConverter.GetBytes((uint)trx.Length).CopyTo(trx, 4);
            BitConverter.GetBytes((ushort)1).CopyTo(trx, 14);
            BitConverter.GetBytes(28u).CopyTo(trx, 16);
            BitConverter.GetBytes(65536u).CopyTo(trx, 20);
            BitConverter.GetBytes(~FsForge.Core.Compression.Crc32.Compute(trx.AsSpan(12))).CopyTo(trx, 8);
            string file = Path.Combine(_dir, "router.trx");
            File.WriteAllBytes(file, trx);

            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog: " + o.GetType().Name);
            Check(await vm.OpenPathAsync(file), "open failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.SquashFs, Firmware.Part.Name: "rootfs", SavesFilesystemOnly: false }
                  && vm.DocumentInfo.StartsWith("TRX · rootfs из 2 · SquashFS"), "status line: " + vm.DocumentInfo);
            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Заголовок TRX" && r.Value.StartsWith("версия 1"))
                                                                    && i.Rows.Any(r => r.Label == "Контрольная сумма данных" && r.Value == "совпадает")
                                                                    && i.Rows.Any(r => r.Label == "▸ rootfs (открыт)" && r.Value.StartsWith("SquashFS"));
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of a TRX image");

            // Ctrl+S writes the TRX image again: the kernel stays, the header gets the new length and CRC.
            vm.Document!.Tree.Remove(vm.Document.Tree.Resolve("/etc/Banner")!);
            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog: " + o.GetType().Name);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            byte[] rebuilt = File.ReadAllBytes(file);
            uint length = BitConverter.ToUInt32(rebuilt, 4);
            Check(length == rebuilt.Length && BitConverter.ToUInt32(rebuilt, 8) == ~FsForge.Core.Compression.Crc32.Compute(rebuilt.AsSpan(12, (int)length - 12))
                  && rebuilt.AsSpan(28, 30000).SequenceEqual(trx.AsSpan(28, 30000)), "the TRX image was not rebuilt as trx lays it out");
            Check(vm.Document is { Firmware.Image: FsForge.Core.Firmware.TrxImage { CrcMatches: true } } && vm.Document.Tree.Resolve("/etc/Banner") is null, "the rebuilt TRX image reopened");

            // "Save as" can still write the filesystem alone, and then the TRX image stays as it is.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsFirmwareContainer && s.WritesDisk && s.ContainerNote.Contains("как их пишет trx"), "save dialog of a TRX image: " + s.ContainerNote);
                s.KeepContainer = false;
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "router-rootfs.squashfs"));
            Check(await vm.SaveAsAsync(), "save as failed: " + string.Join("; ", d.Messages));
            Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(rebuilt) && vm.Document is { Firmware: null }, "the filesystem saved alone, the TRX image unchanged");
            vm.Dispose();
        });

        await Test("TP-Link image: information, rebuilt on save, filesystem saved alone", async () =>
        {
            var (vm, d, _, _) = NewVm();
            byte[] squash = File.ReadAllBytes(MakeImage("tplink.sqfs"));
            // A classic TP-Link image as mktplinkfw -s makes it: the header, the kernel at 0x200, the SquashFS at 64 KiB.
            byte[] img = new byte[0x10000 + squash.Length];
            img.AsSpan(0x200).Fill(0xFF);
            new Random(6).NextBytes(img.AsSpan(0x200, 20000));
            squash.CopyTo(img, 0x10000);
            BigEndian(0x01000000).CopyTo(img, 0);
            Encoding.ASCII.GetBytes("TP-LINK Technologies").CopyTo(img, 4);
            Encoding.ASCII.GetBytes("ver. 1.0").CopyTo(img, 28);
            BigEndian(0x07500001).CopyTo(img, 64);
            BigEndian(1).CopyTo(img, 68);
            uint[] fields = { 0x80060000, 0x80060000, 0x7c0000, 0x200, 20000, 0x10000, (uint)squash.Length, 0, 0 };
            for (int i = 0; i < fields.Length; i++) BigEndian(fields[i]).CopyTo(img, 116 + 4 * i);
            Convert.FromHexString("dcd73aa5c39598fbddf9e7f40eae4738").CopyTo(img, 76);
            System.Security.Cryptography.MD5.HashData(img).CopyTo(img, 76);
            string file = Path.Combine(_dir, "tplink.bin");
            File.WriteAllBytes(file, img);

            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog: " + o.GetType().Name);
            Check(await vm.OpenPathAsync(file), "open failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.SquashFs, Firmware.Part.Name: "rootfs", SavesFilesystemOnly: false }
                  && vm.DocumentInfo.StartsWith("TP-Link · rootfs из 2 · SquashFS"), "status line: " + vm.DocumentInfo);
            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Заголовок TP-Link" && r.Value.StartsWith("версия 1, оборудование 0x07500001"))
                                                                    && i.Rows.Any(r => r.Label == "Производитель" && r.Value == "TP-LINK Technologies")
                                                                    && i.Rows.Any(r => r.Label == "Контрольная сумма MD5" && r.Value == "совпадает");
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of a TP-Link image");

            // Ctrl+S writes the image again as mktplinkfw would: the new rootfs_len and the MD5 with the key.
            vm.Document!.Tree.Remove(vm.Document.Tree.Resolve("/etc/Banner")!);
            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog: " + o.GetType().Name);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            byte[] rebuilt = File.ReadAllBytes(file);
            byte[] sum = rebuilt[76..92], check = (byte[])rebuilt.Clone();
            Convert.FromHexString("dcd73aa5c39598fbddf9e7f40eae4738").CopyTo(check, 76);
            uint rootfsLen = (uint)(rebuilt[140] << 24 | rebuilt[141] << 16 | rebuilt[142] << 8 | rebuilt[143]);
            Check(System.Security.Cryptography.MD5.HashData(check).AsSpan().SequenceEqual(sum) && rootfsLen == rebuilt.Length - 0x10000
                  && rebuilt.AsSpan(0x200, 20000).SequenceEqual(img.AsSpan(0x200, 20000)), "the TP-Link image was not rebuilt as mktplinkfw makes it");
            Check(vm.Document is { Firmware.Image: FsForge.Core.Firmware.TpLinkImage { Md5Matches: true } } && vm.Document.Tree.Resolve("/etc/Banner") is null, "the rebuilt image reopened");

            // "Save as" can still write the filesystem alone, and then the TP-Link image stays as it is.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsFirmwareContainer && s.WritesDisk && s.ContainerNote.Contains("как их пишет mktplinkfw"), "save dialog of a TP-Link image: " + s.ContainerNote);
                s.KeepContainer = false;
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "tplink-rootfs.squashfs"));
            Check(await vm.SaveAsAsync(), "save as failed: " + string.Join("; ", d.Messages));
            Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(rebuilt) && vm.Document is { Firmware: null }, "the filesystem saved alone, the TP-Link image unchanged");
            vm.Dispose();
        });

        await Test("Netgear CHK image: information, rebuilt on save, filesystem saved alone", async () =>
        {
            var (vm, d, _, _) = NewVm();
            byte[] squash = File.ReadAllBytes(MakeImage("chk.sqfs"));
            // As mkchkimg -k kernel -f rootfs -b R7800 makes it: the header with Netgear's checksums, the kernel, the SquashFS.
            static uint Sum(ReadOnlySpan<byte> data)
            {
                uint c0 = 0, c1 = 0;
                foreach (byte b in data) { c0 += b; c1 += c0; }
                uint f0 = (c0 & 0xFFFF) + (c0 >> 16), f1 = (c1 & 0xFFFF) + (c1 >> 16);
                return ((((f1 >> 16) + f1) & 0xFFFF) << 16) | (((f0 >> 16) + f0) & 0xFFFF);
            }
            static byte[] Chk(byte[] kernel, byte[] rootfs)
            {
                byte[] h = new byte[45];
                BigEndian(0x2A23245E).CopyTo(h, 0);
                BigEndian(45).CopyTo(h, 4);
                h[8] = 1; h[9] = 1; h[10] = 1; h[11] = 99;
                BigEndian(Sum(kernel)).CopyTo(h, 16);
                BigEndian(Sum(rootfs)).CopyTo(h, 20);
                BigEndian((uint)kernel.Length).CopyTo(h, 24);
                BigEndian((uint)rootfs.Length).CopyTo(h, 28);
                BigEndian(Sum(kernel.Concat(rootfs).ToArray())).CopyTo(h, 32);
                Encoding.ASCII.GetBytes("R7800").CopyTo(h, 40);
                BigEndian(Sum(h)).CopyTo(h, 36);
                return h.Concat(kernel).Concat(rootfs).ToArray();
            }
            byte[] kernel = new byte[20000];
            new Random(7).NextBytes(kernel);
            byte[] img = Chk(kernel, squash);
            string file = Path.Combine(_dir, "netgear.chk");
            File.WriteAllBytes(file, img);

            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog: " + o.GetType().Name);
            Check(await vm.OpenPathAsync(file), "open failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.SquashFs, Firmware.Part.Name: "rootfs", SavesFilesystemOnly: false }
                  && vm.DocumentInfo.StartsWith("CHK · rootfs из 2 · SquashFS"), "status line: " + vm.DocumentInfo);
            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Плата (Netgear CHK)" && r.Value == "R7800")
                                                                    && i.Rows.Any(r => r.Label == "Контрольные суммы CHK" && r.Value.StartsWith("совпадают"));
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of a CHK image");

            // Ctrl+S writes the image again as mkchkimg would from the same kernel and the new filesystem.
            vm.Document!.Tree.Remove(vm.Document.Tree.Resolve("/etc/Banner")!);
            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog: " + o.GetType().Name);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            byte[] rebuilt = File.ReadAllBytes(file);
            Check(rebuilt.AsSpan().SequenceEqual(Chk(kernel, rebuilt[(45 + kernel.Length)..])), "the CHK image was not rebuilt as mkchkimg makes it");
            Check(vm.Document is { Firmware.Image: FsForge.Core.Firmware.NetgearChkImage { ChkChecksumsValid: true } } && vm.Document.Tree.Resolve("/etc/Banner") is null, "the rebuilt image reopened");

            // "Save as" can still write the filesystem alone, and then the CHK image stays as it is.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsFirmwareContainer && s.WritesDisk && s.ContainerNote.Contains("как их пишет mkchkimg"), "save dialog of a CHK image: " + s.ContainerNote);
                s.KeepContainer = false;
                return s.Accept();
            };
            d.SavePicks.Enqueue(Path.Combine(_dir, "chk-rootfs.squashfs"));
            Check(await vm.SaveAsAsync(), "save as failed: " + string.Join("; ", d.Messages));
            Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(rebuilt) && vm.Document is { Firmware: null }, "the filesystem saved alone, the CHK image unchanged");
            vm.Dispose();
        });

        await Test("FAT boot partition of an SD card: open, edit config.txt, save in place", async () =>
        {
            var (vm, d, _, _) = NewVm();
            // MBR, a FAT16 boot partition at 1 MiB (8 MiB) and the SquashFS of the test image as the root partition.
            const int mib = 1 << 20;
            long p1 = mib, slot1 = 8 * mib, p2 = p1 + slot1;
            using var boot = new FsTree(0x1ED, 1700000000);
            boot.CreateFile(boot.Root, "config.txt", new MemoryFileData(Encoding.ASCII.GetBytes("dtparam=audio=on\n")), 0x1A4, 0, 0, 1700000000);
            boot.CreateFile(boot.Root, "cmdline.txt", new MemoryFileData(Encoding.ASCII.GetBytes("console=serial0,115200 root=/dev/mmcblk0p2 rootwait\n")), 0x1A4, 0, 0, 1700000000);
            boot.CreateFile(boot.CreateDirectory(boot.Root, "overlays"), "Long Overlay Name.dtbo", new MemoryFileData(new byte[5000]), 0x1A4, 0, 0, 1700000000);
            var bootImage = new MemoryStream();
            ImageDocument.WriteTo(boot, bootImage, new SaveOptions
            {
                Format = ImageFormat.Fat,
                Fat = new Core.Fat.FatOptions { Type = Core.Fat.FatType.Fat16, SizeBytes = slot1, Label = "BOOTFS", VolumeId = 0x1234ABCD, HiddenSectors = (uint)(p1 / 512) },
            });
            byte[] root = File.ReadAllBytes(MakeImage("fatsd.root"));
            byte[] disk = new byte[p2 + (root.Length + 0xFFFF) / 0x10000 * 0x10000];
            void Entry(int slot, byte type, long offset, long length)
            {
                Span<byte> e = disk.AsSpan(446 + 16 * slot, 16);
                e[4] = type;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)(offset / 512));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)(length / 512));
            }
            Entry(0, 0x0E, p1, slot1);
            Entry(1, 0x83, p2, disk.Length - p2);
            disk[510] = 0x55; disk[511] = 0xAA;
            bootImage.ToArray().CopyTo(disk, p1);
            root.CopyTo(disk, p2);
            string path = Path.Combine(_dir, "card.img");
            File.WriteAllBytes(path, disk);

            // The root filesystem is preselected; the boot partition can be chosen and edited.
            d.OnDialog = o =>
            {
                var c = (PartitionChoiceViewModel)o;
                Check(c.Rows.Count == 2 && c.Selected is { Partition.Number: 2 }, "the root partition is not preselected: " + c.Selected?.Name);
                Check(c.Rows[0] is { Content: "FAT16 «BOOTFS»", IsEditable: true }, "boot partition row: " + c.Rows[0].Content);
                c.Selected = c.Rows[0];
                return c.Accept();
            };
            Check(await vm.OpenPathAsync(path), "open failed: " + string.Join("; ", d.Messages));
            Check(vm.Document is { Format: ImageFormat.Fat, Container.Partition.Number: 1, Fat: { Type: Core.Fat.FatType.Fat16, Label: "BOOTFS", HiddenSectors: 2048 } }, "the boot partition was not opened");
            Check(vm.DocumentInfo.StartsWith("образ диска MBR · раздел 1 из 2 · FAT16 · кластер ") && vm.DocumentInfo.Contains("свободно"), "status line: " + vm.DocumentInfo);
            Check(!vm.IsInfoOpen, "a FAT partition must open without warnings: " + vm.InfoMessage);
            Check(vm.Items.Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(new[] { "cmdline.txt", "config.txt", "overlays" }), "root of the partition");

            bool infoShown = false;
            d.OnDialog = o => infoShown = o is ImageInfoViewModel i && i.Rows.Any(r => r.Label == "Тип" && r.Value == "FAT16") && i.Rows.Any(r => r.Label == "Метка" && r.Value == "BOOTFS")
                                                                    && i.Rows.Any(r => r.Label == "Серийный номер" && r.Value == "1234-ABCD") && i.Rows.Any(r => r.Label == "Состояние" && r.Value == "чистая");
            vm.ImageInfoCommand.Execute(null);
            Check(infoShown, "image information of a FAT partition");

            // config.txt is replaced, cmdline.txt removed.
            string repl = Path.Combine(_dir, "config-new.txt");
            File.WriteAllText(repl, "dtparam=audio=on\nenable_uart=1\n");
            Select(vm, "config.txt");
            d.FilePicks.Enqueue(new[] { repl });
            await vm.ReplaceContentCommand.ExecuteAsync();
            Select(vm, "cmdline.txt");
            d.Answers.Enqueue(DialogAnswer.Primary);
            await vm.DeleteCommand.ExecuteAsync();

            // "Save as" offers the disk image; the size of the filesystem is that of the partition.
            d.OnDialog = o =>
            {
                var s = (SaveOptionsViewModel)o;
                Check(s.IsFat && s.WritesDisk && !s.FatSizeEditable && s.HasFatSizeNote && s.FatLabel == "BOOTFS" && s.Extension == ".img" && !s.ShowCommonXattrs && !s.ShowAllRoot,
                    "FAT inside the disk image in the save dialog: " + s.Extension + " / " + s.FatSizeNote);
                s.KeepContainer = false;
                Check(!s.WritesDisk && s.FatSizeEditable && s.Extension == ".vfat", "FAT alone: " + s.Extension);
                return false;
            };
            Check(!await vm.SaveAsAsync(), "a cancelled save dialog saved");

            // Ctrl+S writes the partition back in place: everything else stays byte for byte.
            d.OnDialog = o => throw new InvalidOperationException("unexpected dialog on save: " + o.GetType().Name);
            Check(await vm.SaveAsync(), "save failed: " + string.Join("; ", d.Messages));
            byte[] after = File.ReadAllBytes(path);
            Check(after.Length == disk.Length && after.AsSpan(0, (int)p1).SequenceEqual(disk.AsSpan(0, (int)p1)) && after.AsSpan((int)p2).SequenceEqual(disk.AsSpan((int)p2)),
                "the partition table or the root partition changed");
            Check(vm.Document is { Format: ImageFormat.Fat, Container.Partition.Number: 1, Fat: { Type: Core.Fat.FatType.Fat16, Label: "BOOTFS", VolumeId: 0x1234ABCD, HiddenSectors: 2048, SizeBytes: 8 * mib } } && !vm.IsModified,
                "the saved partition: " + vm.DocumentInfo);
            using (var r = new StreamReader(vm.Document!.Tree.Resolve("/config.txt")!.Inode.Data!.OpenRead()))
                Check(r.ReadToEnd() == "dtparam=audio=on\nenable_uart=1\n", "config.txt was not replaced");
            Check(vm.Document.Tree.Resolve("/cmdline.txt") is null && vm.Document.Tree.Resolve("/overlays/Long Overlay Name.dtbo")!.Inode.Size == 5000, "content of the saved partition");
            vm.Dispose();
        });

        await Test("English interface: texts, numbers, plurals, language change notice", async () =>
        {
            try
            {
                L.Set(AppLanguage.English);
                Check(L.IsEnglish && L.T("Отмена") == "Cancel" && L.T("текст без перевода") == "текст без перевода", "translation lookup");
                Check(Fmt.Size(12_700) == "12.4 KB" && Fmt.Size(500) == "500 B", "sizes: " + Fmt.Size(12_700) + ", " + Fmt.Size(500));
                Check(Fmt.Bytes(1_234_567) == "1,234,567", "number grouping: " + Fmt.Bytes(1_234_567));
                Check(Fmt.Plural(1, "файл", "файла", "файлов") == "1 file" && Fmt.Plural(21, "файл", "файла", "файлов") == "21 files", "plurals");
                Check(L.F("Выбрано: {0} из {1}", 2, 5) == "Selected: 2 of 5", "format strings");

                var (vm, d, _, _) = NewVm();
                Check(vm.StatusText == "Open an image or drop it onto the window", "start page: " + vm.StatusText);
                Check(await vm.OpenPathAsync(MakeImage("english.sqfs")), "open failed: " + string.Join("; ", d.Messages));
                Check(vm.StatusText.EndsWith(" objects") && !vm.StatusText.Any(c => c is >= 'А' and <= 'я'), "status line: " + vm.StatusText);
                Check(vm.Items.Any(i => i.TypeText == "Folder"), "type column: " + string.Join(", ", vm.Items.Select(i => i.TypeText)));
                Check(!await vm.OpenPathAsync(Path.Combine(_dir, "missing.img")) && d.Messages.Any(m => m.Contains("File not found")), "missing file: " + string.Join("; ", d.Messages));

                // The notice about a language change is written in the chosen language.
                AppSettings s = vm.Settings.Clone();
                s.Language = AppLanguage.Russian;
                vm.ApplySettings(s);
                Check(d.Notifications.Any(n => n.Contains("Язык интерфейса") && n.Contains("после перезапуска")), "language notice: " + string.Join("; ", d.Notifications));
                vm.Dispose();
            }
            finally
            {
                L.Set(AppLanguage.Russian);
            }
        });

        Console.WriteLine($"{_passed} passed, {_failed} failed");
        if (args.Length == 0)
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
        return _failed == 0 ? 0 : 1;
    }
}
