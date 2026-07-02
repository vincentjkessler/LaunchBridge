(() => {
  const PACKAGE_PATTERN = /\.(zip|devmind|badmoth|council|heightgrid|launchbridge)(?:$|[?#\s])/i;
  let lastSignalKey = '';
  let lastSignalAt = 0;

  function closestClickable(node) {
    if (!node) return null;
    if (node.closest) return node.closest('a,button,[role="button"],[download]');
    return null;
  }

  function readLink(element) {
    if (!element) return '';
    if (element.tagName === 'A') return element.href || element.getAttribute('href') || '';
    const anchor = element.closest && element.closest('a');
    return anchor ? (anchor.href || anchor.getAttribute('href') || '') : '';
  }

  function signalDownloadIntent(event) {
    if (!event || !event.isTrusted) return;
    const element = closestClickable(event.target);
    if (!element) return;

    const href = readLink(element);
    const downloadName = element.getAttribute && (element.getAttribute('download') || '');
    const aria = element.getAttribute && (element.getAttribute('aria-label') || '');
    const text = ((element.innerText || element.textContent || aria || '') + '').trim().slice(0, 400);
    const candidateText = [href, downloadName, text, aria].join(' ');

    const looksLikeFile = Boolean(downloadName) || PACKAGE_PATTERN.test(candidateText) ||
      /download|sandbox|attachment|file/i.test(href) || /download|save file/i.test(aria);
    if (!looksLikeFile) return;

    const key = [href, downloadName, text].join('|');
    const now = Date.now();
    if (key === lastSignalKey && now - lastSignalAt < 1500) return;
    lastSignalKey = key;
    lastSignalAt = now;

    chrome.runtime.sendMessage({
      type: 'launchbridge-download-click',
      href,
      downloadName,
      text,
      pageUrl: location.href,
      pageTitle: document.title,
      clickedAt: now
    }, () => void chrome.runtime.lastError);
  }

  // pointerdown runs before the browser starts the download. click remains as a fallback.
  document.addEventListener('pointerdown', signalDownloadIntent, true);
  document.addEventListener('click', signalDownloadIntent, true);
})();
