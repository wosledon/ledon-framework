const rows = document.querySelector('#rows');
const keyInput = document.querySelector('#key');
const valueInput = document.querySelector('#value');
const effective = document.querySelector('#effective');
const message = document.querySelector('#message');

async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: { 'Content-Type': 'application/json', ...(options.headers ?? {}) },
  });

  if (!response.ok) {
    throw new Error(`${path} → HTTP ${response.status}`);
  }

  return response.status === 204 ? null : response.json();
}

async function loadConfig() {
  const items = await api('/api/config');
  rows.innerHTML = '';

  for (const item of items) {
    const tr = document.createElement('tr');
    const key = document.createElement('td');
    key.innerHTML = `<code>${item.key}</code>`;
    const value = document.createElement('td');
    const pre = document.createElement('pre');
    pre.textContent = JSON.stringify(item.value);
    value.append(pre);
    const actions = document.createElement('td');
    const edit = document.createElement('button');
    edit.className = 'small';
    edit.textContent = '编辑';
    edit.onclick = () => {
      keyInput.value = item.key;
      valueInput.value = JSON.stringify(item.value, null, 2);
    };
    actions.append(edit);
    tr.append(key, value, actions);
    rows.append(tr);
  }

  message.textContent = `已加载 ${items.length} 个配置项`;
}

async function save() {
  const key = keyInput.value.trim();
  const raw = valueInput.value.trim();

  try {
    JSON.parse(raw);
  } catch {
    message.textContent = 'Value 不是合法 JSON';
    return;
  }

  await api(`/api/config/${key}`, { method: 'PUT', body: raw });
  message.textContent = `已保存 ${key}，配置已热更新`;
  await Promise.all([loadConfig(), loadEffective()]);
}

async function remove() {
  const key = keyInput.value.trim();
  await api(`/api/config/${key}`, { method: 'DELETE' });
  message.textContent = `已删除 ${key}`;
  await Promise.all([loadConfig(), loadEffective()]);
}

async function loadEffective() {
  effective.textContent = JSON.stringify(await api('/api/effective'), null, 2);
}

document.querySelector('#refresh').onclick = () => loadConfig().catch((error) => (message.textContent = error.message));
document.querySelector('#effective-refresh').onclick = () => loadEffective().catch((error) => (message.textContent = error.message));
document.querySelector('#save').onclick = () => save().catch((error) => (message.textContent = error.message));
document.querySelector('#delete').onclick = () => remove().catch((error) => (message.textContent = error.message));

loadConfig().catch((error) => (message.textContent = error.message));
loadEffective().catch((error) => (message.textContent = error.message));
