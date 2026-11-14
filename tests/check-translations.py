#!/usr/bin/env python3
"""Checks the English translation of the interface.

Every Russian interface text is a key: L.T("..."), L.F("...", ...), {l:Tr '...'} and <l:TrExtension Text="..."/>
in XAML, the word forms of Fmt.Plural / L.Plural ("файл|файла|файлов") and the size units of Fmt.
The script fails when
  - a key has no entry in src/FsForge.Presentation/Services/EnglishStrings.cs,
  - an entry differs from its key in the {0}-style placeholders, or a menu access key ("_") is lost,
  - a plural entry does not have exactly two forms ("file|files"),
  - Cyrillic text is left in C# code or XAML outside these wrappers (comments excepted).
Entries that no key uses are listed as a warning.

    python3 tests/check-translations.py [--keys]      (--keys prints the keys without a translation)
"""
import glob, os, re, sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
CYR = re.compile("[А-Яа-яЁё]")
CS_STR = r'"((?:[^"\\]|\\.)*)"'


def unescape_cs(s):
    return re.sub(r'\\(.)', lambda m: {'n': '\n', 't': '\t', '0': '\0'}.get(m.group(1), m.group(1)), s)


def unescape_xaml(s):
    s = s.replace("&quot;", '"').replace("&lt;", "<").replace("&gt;", ">").replace("&#x0a;", "\n").replace("&#10;", "\n")
    s = s.replace("&amp;", "&")
    return re.sub(r"\\(.)", r"\1", s)


def strip_comments(code):
    """Removes // and /* */ comments of C# code, keeping string literals intact."""
    out, i, n = [], 0, len(code)
    while i < n:
        if code.startswith("//", i):
            j = code.find("\n", i)
            i = n if j < 0 else j
        elif code.startswith("/*", i):
            i = code.index("*/", i) + 2
        elif code[i] == '"':
            j = i + 1
            while code[j] != '"':
                j += 2 if code[j] == "\\" else 1
            out.append(code[i:j + 1])
            i = j + 1
        elif code[i] == "'":
            j = code.index("'", i + 2 if code[i + 1] == "\\" else i + 1)
            out.append(code[i:j + 1])
            i = j + 1
        else:
            out.append(code[i])
            i += 1
    return "".join(out)


def sources(pattern):
    for p in glob.glob(os.path.join(ROOT, pattern), recursive=True):
        if os.sep + "obj" + os.sep in p or os.sep + "bin" + os.sep in p:
            continue
        yield p


keys, problems = {}, []


def use(key, where):
    keys.setdefault(key, where)


DICT = os.path.join(ROOT, "src/FsForge.Presentation/Services/EnglishStrings.cs")

for path in list(sources("src/FsForge.Presentation/**/*.cs")) + list(sources("src/FsForge.App/**/*.cs")):
    if os.path.samefile(path, DICT):
        continue
    rel = os.path.relpath(path, ROOT)
    code = strip_comments(open(path, encoding="utf-8").read())
    for m in re.finditer(r"\bL\.[TF]\(\s*" + CS_STR, code):
        use(unescape_cs(m.group(1)), rel)
    for m in re.finditer(r"Plural\([^;]*?" + CS_STR + r"\s*,\s*" + CS_STR + r"\s*,\s*" + CS_STR + r"\s*\)", code):
        use("|".join(unescape_cs(m.group(i)) for i in (1, 2, 3)), rel)
    m = re.search(r"Units\s*=\s*\{([^}]*)\}", code)
    if m:
        for u in re.findall(CS_STR, m.group(1)):
            use(u, rel)
    # Cyrillic left outside the wrappers.
    rest = re.sub(r"\bL\.[TF]\(\s*" + CS_STR, "", code)
    rest = re.sub(r"Plural\([^;]*?" + CS_STR + r"\s*,\s*" + CS_STR + r"\s*,\s*" + CS_STR + r"\s*\)", "", rest)
    rest = re.sub(r"Units\s*=\s*\{[^}]*\}", "", rest)
    for no, line in enumerate(rest.splitlines(), 1):
        if CYR.search(line):
            problems.append(f"{rel}: untranslated text: {line.strip()[:120]}")

for path in sources("src/FsForge.App/**/*.xaml"):
    rel = os.path.relpath(path, ROOT)
    text = re.sub(r"<!--.*?-->", "", open(path, encoding="utf-8").read(), flags=re.S)
    for m in re.finditer(r"\{l:Tr '((?:[^'\\]|\\.)*)'\}", text):
        use(unescape_xaml(m.group(1)), rel)
    for m in re.finditer(r'<l:TrExtension Text="([^"]*)"', text):
        use(unescape_xaml(m.group(1)), rel)
    rest = re.sub(r"\{l:Tr '((?:[^'\\]|\\.)*)'\}", "", text)
    rest = re.sub(r'<l:TrExtension Text="([^"]*)"', "", rest)
    for line in rest.splitlines():
        if CYR.search(line):
            problems.append(f"{rel}: untranslated text: {line.strip()[:120]}")

# ---- the dictionary
src = open(DICT, encoding="utf-8").read()
entries = {}
for m in re.finditer(r"\[" + CS_STR + r"\]\s*=\s*" + CS_STR + r"\s*,", src):
    k, v = unescape_cs(m.group(1)), unescape_cs(m.group(2))
    if k in entries:
        problems.append(f"EnglishStrings.cs: duplicate entry {k!r}")
    entries[k] = v

missing = [k for k in keys if k not in entries]
for k in missing:
    problems.append(f"{keys[k]}: no English text for {k!r}")

PH = re.compile(r"\{\d+(?::[^}]*)?\}")
for k, v in entries.items():
    if k not in keys:
        continue
    if CYR.search(v) and v != k:   # an identical entry keeps a name as it is ("Русский")
        problems.append(f"EnglishStrings.cs: the translation of {k!r} is not English: {v!r}")
    if sorted(PH.findall(k)) != sorted(PH.findall(v)):
        problems.append(f"EnglishStrings.cs: placeholders differ for {k!r}: {v!r}")
    if k.count("|") == 2 and "|" not in k.replace("|", "", 2) and v.count("|") != 1 and "*" not in k:
        problems.append(f"EnglishStrings.cs: plural {k!r} needs two forms 'one|many', got {v!r}")
    if "_" in k and re.search(r"_[А-Яа-яЁё]", k) and not re.search(r"_\w", v):
        problems.append(f"EnglishStrings.cs: access key lost in {k!r}: {v!r}")

unused = [k for k in entries if k not in keys]

if "--keys" in sys.argv:
    for k in missing:
        print(repr(k))
    sys.exit(0)

for p in problems:
    print("FAIL  " + p)
for k in unused:
    print(f"note  unused entry {k!r}")
print(f"{len(keys)} texts, {len(entries)} translations, {len(problems)} problem(s), {len(unused)} unused")
sys.exit(1 if problems else 0)
