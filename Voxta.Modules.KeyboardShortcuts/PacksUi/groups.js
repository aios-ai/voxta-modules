'use strict';

const base = '/api/extensions/keyboard-shortcuts/';
const groupList = document.querySelector('#group-list');
const groupTitle = document.querySelector('#group-title');
const groupSummary = document.querySelector('#group-summary');
const shortcuts = document.querySelector('#shortcuts');
const exportGroup = document.querySelector('#export-group');
const status = document.querySelector('#status');
const empty = document.querySelector('#empty');
const table = document.querySelector('#table-wrap');
const importGroup = document.querySelector('#import-group');
const input = document.querySelector('#pack-file');
const dialog = document.querySelector('#preview');
const details = document.querySelector('#preview-details');
const error = document.querySelector('#preview-error');
const apply = document.querySelector('#apply-import');
const close = document.querySelector('#close-preview');
let groups = [];
let selected;
let pending;

function element(tag, text) {
  const node = document.createElement(tag);
  if (text != null) node.textContent = text;
  return node;
}

function groupLabel(group) {
  return group.label;
}

function isEnabled(row) {
  return row.enabled !== false && row.enabled !== 'false';
}

function renderGroups() {
  groupList.replaceChildren();
  for (const group of groups) {
    const button = element('button');
    button.type = 'button';
    button.setAttribute('aria-current', String(selected === group));
    button.append(element('span', groupLabel(group)), element('span', group.pack.shortcuts.length));
    button.addEventListener('click', () => {
      const index = groups.indexOf(group);
      selected = group;
      renderGroups();
      groupList.children[index].focus();
    });
    groupList.append(button);
  }

  shortcuts.replaceChildren();
  empty.hidden = selected !== undefined;
  table.hidden = selected === undefined;
  exportGroup.disabled = !selected?.pack.shortcuts.length;
  groupTitle.textContent = selected ? groupLabel(selected) : 'Shortcuts';
  groupSummary.textContent = selected ? `${selected.pack.shortcuts.length} saved${selected.enabled ? '' : ' · Group disabled'}` : '';
  exportGroup.textContent = selected?.name === null ? 'Export standalone' : 'Export group';
  for (const row of selected?.pack.shortcuts ?? []) {
    const tr = element('tr');
    const name = element('td');
    name.append(element('strong', row.name));
    if (row.description) name.append(element('p', row.description));
    const keys = element('td');
    keys.append(element('kbd', row.keys || '—'));
    tr.append(name, keys, element('td', row.foregroundApp || 'Any app'), element('td', isEnabled(row) ? 'Enabled' : 'Disabled'));
    shortcuts.append(tr);
  }
}

async function request(path, payload) {
  const options = payload == null ? {} : {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  };
  const response = await fetch(base + path, options);
  const result = await response.json().catch(() => null);
  if (!response.ok || result == null) throw new Error(result?.error || `Voxta returned HTTP ${response.status}.`);
  return result;
}

async function reload(preferred = selected?.name) {
  const result = await request('groups');
  groups = result.groups;
  selected = groups.find(group => group.name === preferred || groupLabel(group) === preferred)
    ?? groups.find(group => group.name != null) ?? groups[0];
  renderGroups();
}

exportGroup.addEventListener('click', () => {
  if (!selected) return;
  const url = URL.createObjectURL(new Blob([JSON.stringify(selected.pack, null, 2) + '\n'], { type: 'application/json' }));
  const link = element('a');
  link.href = url;
  link.download = `${selected.name ?? 'standalone'}.voxta-shortcuts.json`;
  link.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
});

function addPreviewSection(title, items) {
  if (items.length === 0) return;
  const section = element('section');
  const list = element('ul');
  for (const item of items) list.append(element('li', item));
  section.append(element('h3', `${title} (${items.length})`), list);
  details.append(section);
}

importGroup.addEventListener('click', () => input.click());
input.addEventListener('change', async () => {
  const file = input.files[0];
  input.value = '';
  if (!file) return;
  importGroup.disabled = true;
  status.textContent = '';
  try {
    if (file.size > 1024 * 1024) throw new Error('The shortcut pack exceeds 1 MB.');
    const pack = JSON.parse(await file.text());
    const result = await request('preview', pack);
    pending = { revision: result.revision, pack };
    details.replaceChildren();
    addPreviewSection('Shortcuts to add', result.added.map(row => {
      const scope = row.foregroundApp || 'Any app';
      const state = isEnabled(row) ? '' : ', disabled';
      return `${row.group || 'Standalone'} / ${row.name}${row.keys ? ` — ${row.keys}` : ''} (${scope}${state})`;
    }));
    addPreviewSection('Already present', result.duplicates);
    addPreviewSection('Conflicts kept as-is', result.conflicts);
    if (!details.children.length) details.append(element('p', 'This pack has no shortcuts.'));
    apply.disabled = result.added.length === 0;
    apply.textContent = `Add ${result.added.length} ${result.added.length === 1 ? 'shortcut' : 'shortcuts'}`;
    error.hidden = true;
    dialog.showModal();
  } catch (failure) {
    status.textContent = failure.message;
  } finally {
    importGroup.disabled = false;
  }
});

close.addEventListener('click', () => dialog.close());
dialog.addEventListener('cancel', event => {
  if (close.disabled) event.preventDefault();
});
dialog.addEventListener('close', () => { pending = undefined; });
apply.addEventListener('click', async () => {
  if (!pending) return;
  apply.disabled = true;
  close.disabled = true;
  error.hidden = true;
  try {
    const result = await request('import', pending);
    dialog.close();
    status.textContent = `Added ${result.added.length} ${result.added.length === 1 ? 'shortcut' : 'shortcuts'}.`;
    await reload(result.added[0]?.group?.trim() || null);
  } catch (failure) {
    if (dialog.open) {
      error.textContent = failure.message;
      error.hidden = false;
    } else {
      status.textContent += ` Could not reload groups. ${failure.message}`;
    }
  } finally {
    close.disabled = false;
  }
});

reload().then(() => { status.textContent = ''; }).catch(failure => { status.textContent = failure.message; });
