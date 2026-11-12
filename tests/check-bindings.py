#!/usr/bin/env python3
"""Static check of the XAML files: every binding path must name an existing property of the view
model it is evaluated against, and every referenced resource key must exist.

The XAML compiler verifies element and property names, but binding paths and resource keys are
only resolved at run time; this script closes that gap without starting the application.

  dotnet run --project tests/FsForge.Presentation.Tests -c Release -- --dump-properties > props.txt
  python3 tests/check-bindings.py props.txt [path-to-wpfui-sources]
"""
import re, sys, os, glob
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.join(HERE, '..', 'src', 'FsForge.App')
P = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'
X = 'http://schemas.microsoft.com/winfx/2006/xaml'

props, elems = {}, {}
for line in open(sys.argv[1], encoding='utf-8'):
    parts = line.rstrip('\n').split('|')
    if len(parts) != 3: continue
    t, p, pt = parts
    if p.endswith('[]'): elems[(t, p[:-2])] = pt
    else: props.setdefault(t, {})[p] = pt
# Base classes: the dump lists inherited members with each type, so no walk is needed.

errors, checked = [], 0

def local(tag): return tag.split('}')[-1]

def split_markup(text):
    """Splits '{Binding A.B, Converter={StaticResource C}}' into its top-level arguments."""
    inner = text.strip()[1:-1]
    name, _, rest = inner.partition(' ')
    args, depth, cur = [], 0, ''
    for ch in rest:
        if ch == '{': depth += 1
        if ch == '}': depth -= 1
        if ch == ',' and depth == 0:
            args.append(cur.strip()); cur = ''
        else: cur += ch
    if cur.strip(): args.append(cur.strip())
    return name, args

def resolve(ctx, path, where):
    global checked
    t = ctx
    for part in path.split('.'):
        if t is None: return None
        if t not in props:
            return None                         # a framework or primitive type: nothing to check
        if part not in props[t]:
            errors.append(f'{where}: no property "{part}" on {t} (binding path "{path}")')
            return None
        checked += 1
        t = props[t][part]
    return t

def check_value(value, ctx, where, keys):
    for m in re.finditer(r'\{(StaticResource|DynamicResource|ui:ThemeResource)\s+([^}{]+?)\}', value):
        keys.append((m.group(2).strip(), where))
    v = value.strip()
    if not v.startswith('{Binding'): return None
    name, args = split_markup(v)
    path, relative = None, False
    for a in args:
        if a.startswith('RelativeSource=') or a.startswith('ElementName=') or a.startswith('Source='): relative = True
        elif a.startswith('Path='): path = a[5:]
        elif '=' not in a.split('{')[0]: path = a
    if relative or path is None or ctx is None: return None
    return resolve(ctx, path, where)

def walk(el, ctx, where, keys):
    tag = local(el.tag)
    file_where = where
    # A data template switches the data context to its item type.
    if tag in ('DataTemplate', 'HierarchicalDataTemplate'):
        dt = el.get('DataType')
        if dt:
            m = re.match(r'\{x:Type\s+vm:([\w.]+)\}', dt)
            ctx = m.group(1) if m else None
        else:
            ctx = getattr(walk, 'item_ctx', None)
    items_type = None
    for attr, value in el.attrib.items():
        a = local(attr)
        if a == 'DisplayMemberBinding': continue      # evaluated against the row type, see below
        w = f'{file_where} <{tag} {a}>'
        result = check_value(value, ctx, w, keys)
        if a == 'ItemsSource' and value.strip().startswith('{Binding') and ctx:
            name, args = split_markup(value.strip())
            path = next((x for x in args if '=' not in x.split('{')[0]), None)
            if path:
                owner = ctx
                parts = path.split('.')
                for part in parts[:-1]:
                    owner = props.get(owner, {}).get(part)
                items_type = elems.get((owner, parts[-1]))
    # Member paths used by combo boxes and menus are evaluated against the item type.
    if items_type:
        for a in ('DisplayMemberPath', 'SelectedValuePath'):
            if el.get(a): resolve(items_type, el.get(a), f'{file_where} <{tag} {a}>')
    # Grid view cells bind against the row type of the enclosing list.
    if tag == 'GridViewColumn' and el.get('DisplayMemberBinding') and getattr(walk, 'item_ctx', None):
        check_value(el.get('DisplayMemberBinding'), walk.item_ctx, f'{file_where} <GridViewColumn DisplayMemberBinding>', keys)
    saved = getattr(walk, 'item_ctx', None)
    if items_type: walk.item_ctx = items_type
    if tag == 'HierarchicalDataTemplate': walk.item_ctx = ctx          # children have the same type
    for child in el:
        ctag = local(child.tag)
        # Things attached to a control but evaluated per item or outside the tree.
        if ctag.endswith('.ItemContainerStyle'):
            walk(child, walk.item_ctx if items_type else ctx, where, keys)
        else:
            walk(child, ctx, where, keys)
    walk.item_ctx = saved

ROOT_CONTEXT = {'MainWindow.xaml': 'MainViewModel', 'DialogWindow.xaml': 'DialogViewModel', 'DialogTemplates.xaml': None, 'App.xaml': None}
keys, defined = [], set()
files = sorted(glob.glob(os.path.join(APP, '**', '*.xaml'), recursive=True))
files = [f for f in files if os.sep + 'obj' + os.sep not in f and os.sep + 'bin' + os.sep not in f]
for f in files:
    text = open(f, encoding='utf-8').read()
    defined |= set(re.findall(r'x:Key="([^"{]+)"', text))
    root = ET.fromstring(text)
    walk.item_ctx = None
    walk(root, ROOT_CONTEXT.get(os.path.basename(f)), os.path.basename(f), keys)

# Resource keys: defined in the application or in the WPF-UI theme dictionaries.
theme = set()
if len(sys.argv) > 2:
    for f in glob.glob(os.path.join(sys.argv[2], '**', '*.xaml'), recursive=True):
        theme |= set(re.findall(r'x:Key="([^"{]+)"', open(f, encoding='utf-8', errors='replace').read()))
missing_keys = 0
for key, where in keys:
    if key.startswith('{x:Type') or key in defined: continue
    if theme and key not in theme:
        errors.append(f'{where}: resource "{key}" is defined neither in the application nor in WPF-UI')
    elif not theme:
        missing_keys += 1

for e in errors: print('ERROR', e)
print(f'{len(files)} files, {checked} binding steps checked, {len(keys)} resource references, {len(errors)} errors'
      + (f' ({missing_keys} theme keys not verified: pass the WPF-UI source folder)' if missing_keys else ''))
sys.exit(1 if errors else 0)
