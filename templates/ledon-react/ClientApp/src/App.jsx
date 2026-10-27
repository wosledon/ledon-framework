import { useEffect, useState } from 'react';

const jsonHeaders = { 'Content-Type': 'application/json' };

export default function App() {
  const [userName, setUserName] = useState('admin');
  const [password, setPassword] = useState('admin123');
  const [token, setToken] = useState(localStorage.getItem('ledon.token') ?? '');
  const [me, setMe] = useState(null);
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!token) {
      setMe(null);
      return;
    }

    fetch('/auth/authorization/me', { headers: { Authorization: `Bearer ${token}` } })
      .then(async (response) => {
        if (!response.ok) throw new Error(`加载身份失败（${response.status}）`);
        return response.json();
      })
      .then(setMe)
      .catch((error) => {
        setToken('');
        localStorage.removeItem('ledon.token');
        setMessage(error.message);
      });
  }, [token]);

  async function login(name = userName, pass = password) {
    setBusy(true);
    setMessage('');

    try {
      const response = await fetch('/auth/login', {
        method: 'POST',
        headers: jsonHeaders,
        body: JSON.stringify({ userName: name, password: pass }),
      });

      if (!response.ok) throw new Error(`登录失败（${response.status}）`);

      const data = await response.json();
      localStorage.setItem('ledon.token', data.accessToken);
      setToken(data.accessToken);
    } catch (error) {
      setMessage(error.message);
    } finally {
      setBusy(false);
    }
  }

  async function callApi(path) {
    setMessage('');

    try {
      const response = await fetch(path, { headers: { Authorization: `Bearer ${token}` } });
      const body = await response.text();
      setMessage(`${path} → ${response.status}\n${body}`);
    } catch (error) {
      setMessage(error.message);
    }
  }

  function logout() {
    localStorage.removeItem('ledon.token');
    setToken('');
    setMe(null);
  }

  return (
    <main>
      <h1>Ledon React + 内置 RBAC</h1>
      <p className="hint">演示账号：admin / admin123（管理员），user / user123（reader）</p>

      {!token ? (
        <section className="card">
          <h2>登录</h2>
          <label>
            用户名
            <input value={userName} onChange={(event) => setUserName(event.target.value)} />
          </label>
          <label>
            密码
            <input type="password" value={password} onChange={(event) => setPassword(event.target.value)} />
          </label>
          <div className="actions">
            <button disabled={busy} onClick={() => login()}>登录</button>
            <button disabled={busy} onClick={() => login('admin', 'admin123')}>管理员演示</button>
            <button disabled={busy} onClick={() => login('user', 'user123')}>普通用户演示</button>
          </div>
        </section>
      ) : (
        <section className="card">
          <div className="actions">
            <button onClick={logout}>退出</button>
            <button onClick={() => callApi('/api/weather')}>读取天气（weather.read）</button>
            <button onClick={() => callApi('/api/admin')}>管理员接口（authorization.manage）</button>
          </div>

          {me && (
            <div className="identity">
              <h2>{me.userId}</h2>
              <p>角色：{me.roles.join(', ') || '（无）'}</p>
              <p>权限：{me.permissions.join(', ') || '（无）'}</p>
              <p>管理员：{me.isAdmin ? '是' : '否'}</p>
            </div>
          )}
        </section>
      )}

      {message && <pre className="message">{message}</pre>}
    </main>
  );
}
