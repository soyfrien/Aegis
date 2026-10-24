(() => {
  const $ = (id) => document.getElementById(id);
  const MAX_BYTES = 1048576;

  const showError = (msg) => { $('error').textContent = msg || ''; $('error').hidden = !msg; };
  const setMsg = (msg, bad) => { const m = $('save-msg'); m.textContent = msg || ''; m.className = 'muted' + (bad ? ' bad' : ''); };

  async function api(method, path, body) {
    const res = await fetch('/api' + path, {
      method, credentials: 'same-origin',
      headers: body ? { 'Content-Type': 'application/json' } : {},
      body: body ? JSON.stringify(body) : undefined,
    });
    if (res.status === 401) { $('content').hidden = true; $('login-hint').hidden = false; throw new Error('Please unlock Aegis first.'); }
    let data = null;
    try { data = await res.json(); } catch { /* empty */ }
    if (!res.ok) throw new Error(res.status === 429 ? 'Too many requests, wait a minute.' : (data && data.error) || `Request failed (${res.status})`);
    return data;
  }

  function render(info) {
    const status = $('status');
    const ul = $('sites'); ul.textContent = '';
    $('delete-all').hidden = !(info.configured && info.source === 'uploaded');
    $('server-note').hidden = info.source !== 'server-config';
    if (!info.configured) { status.textContent = 'No cookies saved. Public videos work without them.'; return; }
    const when = info.savedAt ? new Date(info.savedAt).toLocaleString() : 'unknown';
    status.textContent = `${info.totalCookies} cookies saved for ${info.sites.length} site${info.sites.length === 1 ? '' : 's'} · last saved ${when}`;
    for (const s of info.sites) {
      const li = document.createElement('li');
      const head = document.createElement('div'); head.className = 'sitehead';
      const name = document.createElement('strong'); name.textContent = s.label;
      head.append(name, ` · ${s.count} cookie${s.count === 1 ? '' : 's'}`);
      const doms = document.createElement('div'); doms.className = 'muted'; doms.textContent = s.domains.join(', ');
      li.append(head, doms);
      if (info.source === 'uploaded') {
        const b = document.createElement('button'); b.type = 'button'; b.className = 'secondary small'; b.textContent = 'Remove ' + s.label;
        b.addEventListener('click', async () => {
          if (!confirm('Remove the saved cookies for ' + s.label + '?')) return;
          try { render(await api('DELETE', '/cookies?site=' + encodeURIComponent(s.site))); setMsg('Removed.'); } catch (e) { showError(e.message); }
        });
        li.append(b);
      }
      ul.append(li);
    }
  }

  async function load() {
    try { render(await api('GET', '/cookies')); $('login-hint').hidden = true; $('content').hidden = false; showError(''); }
    catch (e) { if ($('login-hint').hidden) showError(e.message); }
  }

  $('cookie-file').addEventListener('change', async (ev) => {
    const f = ev.target.files && ev.target.files[0];
    if (!f) return;
    if (f.size > MAX_BYTES) { setMsg('That file is too large (limit 1 MB).', true); ev.target.value = ''; return; }
    try { $('cookie-text').value = await f.text(); setMsg(`Loaded ${f.name}. Tap “Save cookies”.`); }
    catch { setMsg('Could not read that file.', true); }
  });

  $('save-cookies').addEventListener('click', async () => {
    const text = $('cookie-text').value;
    if (!text.trim()) { setMsg('Paste your cookies.txt or choose a file first.', true); return; }
    if (text.length > MAX_BYTES) { setMsg('That is too large (limit 1 MB).', true); return; }
    $('save-cookies').disabled = true; setMsg('Saving…');
    try {
      const info = await api('PUT', '/cookies', { content: text, replaceAll: $('replace-all').checked });
      $('cookie-text').value = ''; $('cookie-file').value = ''; $('replace-all').checked = false;
      render(info); setMsg('Saved. The text box was cleared; cookie values are never shown again.');
    } catch (e) { setMsg(e.message, true); }
    finally { $('save-cookies').disabled = false; }
  });

  $('delete-all').addEventListener('click', async () => {
    if (!confirm('Delete ALL saved cookies from the server?')) return;
    try { render(await api('DELETE', '/cookies')); setMsg('All cookies deleted.'); } catch (e) { showError(e.message); }
  });

  load();
  if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(() => { /* not fatal */ });
})();
