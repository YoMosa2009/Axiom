(() => {
  const copyBtn = document.getElementById('copy-winget-btn');
  const statusEl = document.getElementById('copy-winget-status');
  const wingetCommand = 'winget install YoMosa2009.Axiom';

  if (!copyBtn || !statusEl) {
    return;
  }

  copyBtn.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(wingetCommand);
      statusEl.textContent = 'Copied command';
    } catch {
      statusEl.textContent = wingetCommand;
    }
  });
})();
