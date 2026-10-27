const state = {
  token: localStorage.getItem('ledon.config.token') ?? '',
  projects: [],
  project: null,
  environment: 'dev',
  permissions: [],
};

const $ = (id) => document.getElementById(id);

async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      ...(state.token ? { Authorization: `Bearer ${state.token}` } : {}),
      ...(options.headers ?? {}),
    },
  });

  if (response.status === 401) {
    logout();
    throw new Error('登录已过期，请重新登录');
  }

  if (!response.ok) {
    const text = await response.text();
    throw new Error(`${options.method ?? 'GET'} ${path} → ${response.status}${text ? `: ${text}` : ''}`);
  }

  return response.status === 204 ? null : response.json();
}

function showMessage(text, isError = false) {
  const element = $('message');
  element.textContent = text;
  element.className = `message ${isError ? 'error' : ''}`;
}

async function login(userName = $('username').value, password = $('password').value) {
  $('login-error').textContent = '';

  try {
    const response = await fetch('/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userName, password }),
    });

    if (!response.ok) {
      $('login-error').textContent = `登录失败（${response.status}）`;
      return;
    }

    const token = await response.json();
    state.token = token.accessToken;
    localStorage.setItem('ledon.config.token', state.token);
    await enterApp();
  } catch (error) {
    $('login-error').textContent = error.message;
  }
}

function logout() {
  state.token = '';
  state.projects = [];
  state.project = null;
  localStorage.removeItem('ledon.config.token');
  $('login-panel').classList.remove('hidden');
  $('app-panel').classList.add('hidden');
  $('user-area').classList.add('hidden');
}

async function enterApp() {
  const me = await api('/api/me');
  $('who').textContent = `${me.userName ?? me.userId}（${me.userId}）`;
  $('login-panel').classList.add('hidden');
  $('app-panel').classList.remove('hidden');
  $('user-area').classList.remove('hidden');
  await loadProjects();
}

async function loadProjects() {
  state.projects = await api('/api/projects');
  const list = $('projects');
  list.innerHTML = '';

  for (const project of state.projects) {
    const item = document.createElement('div');
    item.className = `project${state.project?.id === project.id ? ' active' : ''}`;
    item.innerHTML = `<strong></strong><small></small>`;
    item.querySelector('strong').textContent = project.name;
    item.querySelector('small').textContent = `${project.key} · ${project.role}`;
    item.onclick = () => selectProject(project.id).catch((error) => showMessage(error.message, true));
    list.append(item);
  }

  if (!state.project && state.projects.length > 0)
    await selectProject(state.projects[0].id);
}

async function selectProject(projectId) {
  state.project = await api(`/api/projects/${projectId}`);
  state.environment = state.project.environments[0]?.name ?? 'dev';

  const detail = state.project;
  $('project-panel').classList.remove('hidden');
  $('project-name').textContent = detail.name;
  $('project-meta').textContent = `${detail.key} · 创建者 ${detail.createdBy}`;
  $('permissions').textContent = `当前角色：${detail.role} · 权限：${detail.permissions.join(', ')}`;
  $('delete-project').classList.toggle('hidden', !state.project.permissions.includes('project.manage'));

  const envSelect = $('environment');
  envSelect.innerHTML = '';
  for (const environment of detail.environments) {
    const option = document.createElement('option');
    option.value = environment.name;
    option.textContent = environment.name;
    envSelect.append(option);
  }
  envSelect.value = state.environment;
  envSelect.disabled = detail.environments.length === 0;

  $('members-panel').classList.toggle('hidden', !detail.permissions.includes('members.manage'));
  await Promise.all([loadConfigs(), loadMembers()]);
  await loadProjects();
}

async function loadConfigs() {
  const configs = await api(`/api/projects/${state.project.id}/configs?environment=${encodeURIComponent(state.environment)}`);
  const rows = $('configs');
  rows.innerHTML = '';

  for (const config of configs) {
    const tr = document.createElement('tr');
    const key = document.createElement('td');
    const code = document.createElement('code');
    code.textContent = config.key;
    key.append(code);

    const value = document.createElement('td');
    const pre = document.createElement('pre');
    pre.textContent = JSON.stringify(config.value);
    value.append(pre);

    const version = document.createElement('td');
    version.textContent = `v${config.version}`;

    const actions = document.createElement('td');
    const edit = document.createElement('button');
    edit.className = 'small ghost';
    edit.textContent = '编辑';
    edit.onclick = () => {
      $('config-key').value = config.key;
      $('config-value').value = JSON.stringify(config.value, null, 2);
      $('history').classList.add('hidden');
    };
    actions.append(edit);

    tr.append(key, value, version, actions);
    rows.append(tr);
  }

  if (configs.length === 0) {
    const tr = document.createElement('tr');
    tr.innerHTML = '<td colspan="4" class="hint">当前环境还没有配置项</td>';
    rows.append(tr);
  }
}

async function loadMembers() {
  if (!state.project.permissions.includes('members.manage'))
    return;

  const members = await api(`/api/projects/${state.project.id}/members`);
  const rows = $('members');
  rows.innerHTML = '';

  for (const member of members) {
    const tr = document.createElement('tr');
    const user = document.createElement('td');
    user.textContent = member.userId;
    const role = document.createElement('td');
    role.textContent = member.role;
    const actions = document.createElement('td');
    const remove = document.createElement('button');
    remove.className = 'small danger';
    remove.textContent = '移除';
    remove.onclick = () => removeMember(member.userId).catch((error) => showMessage(error.message, true));
    actions.append(remove);
    tr.append(user, role, actions);
    rows.append(tr);
  }
}

async function removeMember(userId) {
  await api(`/api/projects/${state.project.id}/members/${encodeURIComponent(userId)}`, { method: 'DELETE' });
  showMessage(`已移除成员 ${userId}`);
  await selectProject(state.project.id);
}

async function createProject() {
  const key = $('new-project-key').value.trim();
  const name = $('new-project-name').value.trim();
  const description = $('new-project-desc').value.trim();

  if (!key || !name) {
    showMessage('项目 Key 和名称不能为空', true);
    return;
  }

  const created = await api('/api/projects', {
    method: 'POST',
    body: JSON.stringify({ key, name, description: description || null }),
  });

  $('new-project-key').value = '';
  $('new-project-name').value = '';
  $('new-project-desc').value = '';
  showMessage(`项目已创建：${created.key}`);
  await loadProjects();
  await selectProject(created.id);
}

async function deleteProject() {
  if (!confirm(`确认删除项目 ${state.project.name}？配置与成员会一并删除。`))
    return;

  await api(`/api/projects/${state.project.id}`, { method: 'DELETE' });
  state.project = null;
  $('project-panel').classList.add('hidden');
  showMessage('项目已删除');
  await loadProjects();
}

async function saveConfig() {
  const key = $('config-key').value.trim();
  if (!key) {
    showMessage('配置 Key 不能为空', true);
    return;
  }

  let value;
  try {
    value = JSON.parse($('config-value').value);
  } catch {
    showMessage('Value 不是合法 JSON', true);
    return;
  }

  await api(`/api/projects/${state.project.id}/configs/${encodeURIComponent(key)}?environment=${encodeURIComponent(state.environment)}`, {
    method: 'PUT',
    body: JSON.stringify({ value }),
  });

  showMessage(`已保存 ${key}`);
  await loadConfigs();
}

async function deleteConfig() {
  const key = $('config-key').value.trim();
  if (!key) {
    showMessage('配置 Key 不能为空', true);
    return;
  }

  await api(`/api/projects/${state.project.id}/configs/${encodeURIComponent(key)}?environment=${encodeURIComponent(state.environment)}`, { method: 'DELETE' });
  showMessage(`已删除 ${key}`);
  await loadConfigs();
}

async function loadHistory() {
  const key = $('config-key').value.trim();
  if (!key)
    return;

  const revisions = await api(
    `/api/projects/${state.project.id}/configs/${encodeURIComponent(key)}/history?environment=${encodeURIComponent(state.environment)}`);

  $('history').textContent = revisions.map((revision) =>
    `v${revision.version}  ${revision.updatedBy}  ${revision.createdAtUtc}\n${JSON.stringify(revision.value, null, 2)}`).join('\n\n');
  $('history').classList.remove('hidden');
}

async function addMember() {
  const userId = $('member-user').value.trim();
  const role = $('member-role').value;

  if (!userId) {
    showMessage('成员 UserId 不能为空', true);
    return;
  }

  await api(`/api/projects/${state.project.id}/members`, {
    method: 'POST',
    body: JSON.stringify({ userId, role }),
  });

  $('member-user').value = '';
  showMessage(`已保存成员 ${userId}`);
  await loadMembers();
}

$('login').onclick = () => login();
$('login-user').onclick = () => login('user', 'user123');
$('login-viewer').onclick = () => login('viewer', 'viewer123');
$('logout').onclick = logout;
$('refresh-projects').onclick = () => loadProjects().catch((error) => showMessage(error.message, true));
$('create-project').onclick = () => createProject().catch((error) => showMessage(error.message, true));
$('delete-project').onclick = () => deleteProject().catch((error) => showMessage(error.message, true));
$('environment').onchange = (event) => {
  state.environment = event.target.value;
  loadConfigs().catch((error) => showMessage(error.message, true));
};
$('refresh-configs').onclick = () => loadConfigs().catch((error) => showMessage(error.message, true));
$('save-config').onclick = () => saveConfig().catch((error) => showMessage(error.message, true));
$('delete-config').onclick = () => deleteConfig().catch((error) => showMessage(error.message, true));
$('history-config').onclick = () => loadHistory().catch((error) => showMessage(error.message, true));
$('add-member').onclick = () => addMember().catch((error) => showMessage(error.message, true));

if (state.token)
  enterApp().catch((error) => {
    logout();
    $('login-error').textContent = error.message;
  });
