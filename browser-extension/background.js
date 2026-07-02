const NATIVE_HOST = 'com.launchbridge.smartclick';
const CLICK_WINDOW_MS = 60000;
const RETRO_MATCH_WINDOW_MS = 15000;
const STATE_KEY = 'launchbridgeSmartClickStateV2';
const APPROVED_HOSTS = [
  'chatgpt.com',
  'chat.openai.com',
  'gemini.google.com',
  'claude.ai'
];

let statePromise = null;

function nowIso() {
  return new Date().toISOString();
}

function approvedUrl(value) {
  if (!value) return false;
  try {
    const host = new URL(value).hostname.toLowerCase();
    return APPROVED_HOSTS.some((approved) => host === approved || host.endsWith('.' + approved));
  } catch (_) {
    return false;
  }
}

function cleanState(raw) {
  const now = Date.now();
  const pending = Array.isArray(raw && raw.pendingClicks) ? raw.pendingClicks : [];
  const tracked = raw && typeof raw.trackedDownloads === 'object' && raw.trackedDownloads
    ? raw.trackedDownloads : {};

  return {
    pendingClicks: pending.filter((item) => item && now - Number(item.clickedAt || 0) <= CLICK_WINDOW_MS),
    trackedDownloads: tracked,
    lastEvent: raw && raw.lastEvent ? raw.lastEvent : null
  };
}

async function loadState() {
  if (!statePromise) {
    statePromise = chrome.storage.session.get(STATE_KEY).then((result) => {
      return cleanState(result && result[STATE_KEY]);
    }).catch(() => cleanState(null));
  }
  return statePromise;
}

async function saveState(state) {
  const cleaned = cleanState(state);
  statePromise = Promise.resolve(cleaned);
  await chrome.storage.session.set({ [STATE_KEY]: cleaned });
}

async function setBadge(text, color) {
  try {
    await chrome.action.setBadgeText({ text });
    if (color) await chrome.action.setBadgeBackgroundColor({ color });
  } catch (_) {}
}

async function notify(title, message) {
  try {
    await chrome.notifications.create({
      type: 'basic',
      iconUrl: 'icons/icon128.png',
      title,
      message
    });
  } catch (_) {}
}

function fileHint(value) {
  const text = String(value || '').toLowerCase();
  const match = text.match(/([^/?#\\]+\.(?:zip|devmind|badmoth|council|heightgrid|launchbridge))(?:$|[?#\s])/i);
  return match ? match[1].toLowerCase() : '';
}

function scoreClickForDownload(click, item) {
  if (!click || !item) return -1;
  const started = Date.parse(item.startTime || '') || Date.now();
  const delta = Math.abs(started - Number(click.clickedAt || 0));
  if (delta > CLICK_WINDOW_MS) return -1;

  let score = Math.max(0, 100 - Math.floor(delta / 250));
  const clickName = fileHint(click.downloadName) || fileHint(click.text) || fileHint(click.href);
  const itemName = fileHint(item.filename) || fileHint(item.finalUrl) || fileHint(item.url);
  if (clickName && itemName && clickName === itemName) score += 200;
  if (click.href && (item.url === click.href || item.finalUrl === click.href)) score += 300;
  if (approvedUrl(click.pageUrl)) score += 50;
  return score;
}

async function recordEvent(kind, details) {
  const state = await loadState();
  state.lastEvent = { kind, details: details || '', at: nowIso() };
  await saveState(state);
}

async function chooseClick(item) {
  const state = await loadState();
  let bestIndex = -1;
  let bestScore = -1;
  state.pendingClicks.forEach((click, index) => {
    const score = scoreClickForDownload(click, item);
    if (score > bestScore) {
      bestScore = score;
      bestIndex = index;
    }
  });

  let click = null;
  if (bestIndex >= 0) click = state.pendingClicks.splice(bestIndex, 1)[0];
  await saveState(state);
  return click;
}

async function trackDownload(item, click, reason) {
  if (!item || typeof item.id !== 'number') return;
  const state = await loadState();
  state.trackedDownloads[String(item.id)] = {
    click: click || {
      pageUrl: item.referrer || '',
      pageTitle: '',
      text: '',
      href: item.url || '',
      clickedAt: Date.parse(item.startTime || '') || Date.now()
    },
    reason: reason || 'matched',
    trackedAt: Date.now()
  };
  state.lastEvent = { kind: 'tracked', details: `${item.id}:${reason || 'matched'}`, at: nowIso() };
  await saveState(state);
  await setBadge('…', '#167b83');

  if (item.state === 'complete') await routeCompletedDownload(item.id);
}

async function retroactivelyMatchRecentDownload(click) {
  let items = [];
  try {
    items = await chrome.downloads.search({ orderBy: ['-startTime'], limit: 12 });
  } catch (_) {
    return;
  }

  const state = await loadState();
  const trackedIds = state.trackedDownloads || {};
  let best = null;
  let bestScore = -1;
  for (const item of items || []) {
    if (!item || trackedIds[String(item.id)]) continue;
    const started = Date.parse(item.startTime || '') || 0;
    if (!started || Math.abs(started - Number(click.clickedAt || 0)) > RETRO_MATCH_WINDOW_MS) continue;
    const score = scoreClickForDownload(click, item);
    if (score > bestScore) {
      best = item;
      bestScore = score;
    }
  }
  if (best) await trackDownload(best, click, 'retroactive-click-match');
}

async function routeCompletedDownload(downloadId) {
  const state = await loadState();
  const tracked = state.trackedDownloads[String(downloadId)];
  if (!tracked) return;

  let items = [];
  try {
    items = await chrome.downloads.search({ id: Number(downloadId) });
  } catch (error) {
    await recordEvent('download-search-failed', error && error.message ? error.message : String(error));
    return;
  }
  if (!items || !items.length || !items[0].filename) return;
  const item = items[0];
  if (item.state !== 'complete') return;

  delete state.trackedDownloads[String(downloadId)];
  state.lastEvent = { kind: 'sending-native', details: item.filename, at: nowIso() };
  await saveState(state);

  const click = tracked.click || {};
  try {
    const response = await chrome.runtime.sendNativeMessage(NATIVE_HOST, {
      action: 'openDownloadedBuild',
      path: item.filename,
      sourceSite: click.pageUrl || item.referrer || '',
      sourceTitle: click.pageTitle || '',
      clickedText: click.text || '',
      downloadUrl: item.finalUrl || item.url || '',
      userInitiated: true,
      completedAt: nowIso()
    });

    if (response && response.ok) {
      await setBadge('OK', '#207a43');
      await recordEvent('opened', item.filename);
      await notify('Opening with LaunchBridge', response.fileName || 'Your downloaded app is ready.');
    } else if (response && response.ignored) {
      await setBadge('', '#167b83');
      await recordEvent('ignored', response.message || item.filename);
    } else {
      await setBadge('!', '#b02d2d');
      await recordEvent('native-rejected', response && response.message ? response.message : item.filename);
      await notify('LaunchBridge could not open this download',
        (response && response.message) || 'Open LaunchBridge and check Smart Click.');
    }
  } catch (error) {
    await setBadge('!', '#b02d2d');
    await recordEvent('native-error', error && error.message ? error.message : String(error));
    await notify('LaunchBridge is not connected',
      error && error.message ? error.message : 'Open LaunchBridge and run Smart Click setup.');
  }
}

chrome.runtime.onMessage.addListener((message, sender) => {
  if (!message || message.type !== 'launchbridge-download-click') return;
  (async () => {
    const click = {
      href: message.href || '',
      text: message.text || '',
      downloadName: message.downloadName || '',
      pageUrl: message.pageUrl || '',
      pageTitle: message.pageTitle || '',
      clickedAt: Number(message.clickedAt) || Date.now(),
      tabId: sender.tab ? sender.tab.id : -1
    };
    const state = await loadState();
    state.pendingClicks.push(click);
    state.lastEvent = { kind: 'click-seen', details: click.text || click.href, at: nowIso() };
    await saveState(state);
    await setBadge('1', '#167b83');
    await retroactivelyMatchRecentDownload(click);
  })().catch(() => {});
});

chrome.downloads.onCreated.addListener((item) => {
  (async () => {
    const click = await chooseClick(item);
    if (click) {
      await trackDownload(item, click, 'click-match');
      return;
    }

    // This catches downloads where ChatGPT starts the download before the page message arrives.
    if (approvedUrl(item.referrer)) {
      await trackDownload(item, null, 'approved-referrer');
    }
  })().catch(() => {});
});

chrome.downloads.onChanged.addListener((delta) => {
  if (!delta || !delta.state || delta.state.current !== 'complete') return;
  routeCompletedDownload(delta.id).catch(() => {});
});

chrome.runtime.onStartup.addListener(() => {
  (async () => {
    await setBadge('', '#167b83');
    const state = await loadState();
    for (const id of Object.keys(state.trackedDownloads || {})) {
      await routeCompletedDownload(Number(id));
    }
  })().catch(() => {});
});

chrome.runtime.onInstalled.addListener(() => {
  setBadge('ON', '#207a43').catch(() => {});
});

chrome.action.onClicked.addListener(() => {
  (async () => {
    try {
      const response = await chrome.runtime.sendNativeMessage(NATIVE_HOST, { action: 'ping' });
      if (response && response.ok) {
        await setBadge('ON', '#207a43');
        await notify('Smart Click is connected', 'Click an app download link in ChatGPT and LaunchBridge will receive it.');
      } else {
        throw new Error((response && response.message) || 'The native helper did not answer.');
      }
    } catch (error) {
      await setBadge('!', '#b02d2d');
      await notify('Smart Click is not connected', error && error.message ? error.message : 'Run Smart Click setup in LaunchBridge.');
    }
  })().catch(() => {});
});
