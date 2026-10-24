(() => {
  const origin = location.origin;
  document.getElementById('tpl').textContent = origin + '/share?url=';
  document.getElementById('fetch').textContent = origin + '/api/fetch';

  document.querySelectorAll('button[data-copy]').forEach((btn) => {
    btn.addEventListener('click', async () => {
      const text = document.getElementById(btn.dataset.copy).textContent;
      try { await navigator.clipboard.writeText(text); btn.textContent = 'Copied'; }
      catch { btn.textContent = 'Select & copy'; }
      setTimeout(() => { btn.textContent = 'Copy'; }, 1800);
    });
  });
})();
