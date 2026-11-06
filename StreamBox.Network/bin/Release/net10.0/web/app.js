import { StreamPlayer } from './player.js';

const $ = (id) => document.getElementById(id);

const els = {
  screen: $('screen'),
  video: $('video'),
  hostName: $('host-name'),
  linkDot: $('link-dot'),
  idle: $('panel-idle'),
  idleDetail: $('idle-detail'),
  ready: $('panel-ready'),
  readyTitle: $('ready-title'),
  readyDetail: $('ready-detail'),
  error: $('panel-error'),
  errorDetail: $('error-detail'),
  btnPlay: $('btn-play'),
  btnRetry: $('btn-retry'),
  btnDisconnect: $('btn-disconnect'),
  btnCloseOverlay: $('btn-close-overlay'),
  spinner: $('spinner'),
  spinnerLabel: $('spinner-label'),
  overlay: $('overlay'),
  hud: $('hud'),
  chipFps: $('chip-fps'),
  chipBitrate: $('chip-bitrate'),
  chipBuffer: $('chip-buffer'),
  info: {
    host: $('info-host'),
    window: $('info-window'),
    resolution: $('info-resolution'),
    codec: $('info-codec'),
    encoder: $('info-encoder'),
    fps: $('info-fps'),
    bitrate: $('info-bitrate'),
    buffer: $('info-buffer'),
    viewers: $('info-viewers'),
    uptime: $('info-uptime'),
  },
};

const CODEC_FALLBACKS = [
  'video/mp4; codecs="avc1.64002A,mp4a.40.2"',
  'video/mp4; codecs="avc1.4D402A,mp4a.40.2"',
  'video/mp4; codecs="avc1.42E02A,mp4a.40.2"',
  'video/mp4; codecs="avc1.640028,mp4a.40.2"',
];

const params = new URLSearchParams(location.search);
const deviceName = params.get('name') || 'TV Box';

const player = new StreamPlayer(els.video);

let status = null;
let viewState = 'idle';
let overlayTimer = 0;
let hudTimer = 0;
let pollTimer = 0;

// ------------------------------------------------------------------ estado

function setState(next, detail) {
  viewState = next;
  els.screen.dataset.state = next;

  els.idle.hidden = next !== 'idle';
  els.ready.hidden = next !== 'ready';
  els.error.hidden = next !== 'error';

  els.spinner.hidden = next !== 'connecting';
  els.hud.hidden = next !== 'playing';

  if (next === 'error' && detail) els.errorDetail.textContent = detail;
  if (next === 'idle' && detail) els.idleDetail.textContent = detail;
  if (next === 'connecting' && detail) els.spinnerLabel.textContent = detail;

  if (next !== 'playing') closeOverlay();
}

function wsUrl() {
  const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
  return `${scheme}://${location.host}/ws/stream?name=${encodeURIComponent(deviceName)}`;
}

function formatKbps(kbps) {
  if (!kbps) return '—';
  return kbps >= 1000 ? `${(kbps / 1000).toFixed(1)} Mbps` : `${Math.round(kbps)} kbps`;
}

function formatUptime(seconds) {
  if (!seconds) return '—';
  const total = Math.round(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h}h ${String(m).padStart(2, '0')}min`;
  return `${m}min ${String(s).padStart(2, '0')}s`;
}

// ------------------------------------------------------------------ status

async function poll() {
  try {
    const response = await fetch('/api/status', { cache: 'no-store' });
    if (!response.ok) throw new Error('HTTP ' + response.status);
    const data = await response.json();
    applyStatus(data);
  } catch (_) {
    els.linkDot.dataset.link = 'offline';
    els.hostName.textContent = 'Computador';
    if (viewState === 'idle') {
      els.idleDetail.textContent = 'Sem conexão com o computador. Verifique se o StreamBox está aberto na mesma rede.';
    }
  } finally {
    pollTimer = window.setTimeout(poll, viewState === 'playing' ? 4000 : 1500);
  }
}

function applyStatus(data) {
  status = data;

  if (data.machineName) els.hostName.textContent = data.machineName;
  els.linkDot.dataset.link = data.streaming ? 'streaming' : 'online';

  if (data.streaming) {
    els.readyTitle.textContent = data.windowTitle || 'Janela do computador';
    els.readyDetail.textContent =
      `${data.width}×${data.height} · ${data.targetFps} FPS · ${formatKbps(data.bitrateKbps)}` +
      (data.audio ? ' · áudio' : ' · sem áudio');

    if (viewState === 'idle' || viewState === 'error') {
      void attemptStart(false);
    }
  } else {
    if (viewState === 'playing' || viewState === 'connecting') {
      player.stop();
      setState('idle', 'A transmissão foi encerrada no computador.');
    } else if (viewState === 'idle') {
      els.idleDetail.textContent = 'Conectado. Inicie uma transmissão no computador.';
    }
  }

  if (viewState === 'playing') updateInfo();
}

// --------------------------------------------------------------- reprodução

let startGuard = 0;

async function attemptStart(userGesture) {
  if (!status || !status.streaming) return;
  const now = Date.now();
  if (now - startGuard < 1200) return;
  startGuard = now;

  setState('connecting', 'Conectando à transmissão…');

  const codecs = status.mseCodec ? [status.mseCodec].concat(CODEC_FALLBACKS) : CODEC_FALLBACKS;
  await player.start(wsUrl(), codecs);

  if (userGesture) {
    try { await els.video.play(); } catch (_) {}
  }

  window.setTimeout(() => {
    if (viewState === 'connecting' && (els.video.paused || els.video.readyState < 2)) {
      // Autoplay com áudio foi bloqueado: exige um gesto explícito do controle remoto.
      setState('ready');
      els.btnPlay.textContent = 'Assistir agora';
      els.btnPlay.focus();
    }
  }, 2500);
}

function disconnect() {
  player.stop();
  setState(status && status.streaming ? 'ready' : 'idle');
}

// ------------------------------------------------------------------ overlay

function openOverlay() {
  updateInfo();
  els.overlay.hidden = false;
  els.btnCloseOverlay.focus();
  window.clearTimeout(overlayTimer);
  overlayTimer = window.setTimeout(closeOverlay, 7000);
}

function closeOverlay() {
  window.clearTimeout(overlayTimer);
  els.overlay.hidden = true;
  if (viewState === 'playing') els.video.focus();
}

function toggleOverlay() {
  if (els.overlay.hidden) openOverlay();
  else closeOverlay();
}

function updateInfo() {
  if (!status) return;
  const stats = player.stats;
  const fps = status.measuredFps || 0;

  els.info.host.textContent = status.machineName || location.hostname;
  els.info.window.textContent = status.windowTitle || '—';
  els.info.resolution.textContent = status.width ? `${status.width}×${status.height}` : '—';
  els.info.codec.textContent = status.codec || player.codec || '—';
  els.info.encoder.textContent = status.encoder || '—';
  els.info.fps.textContent = fps ? `${fps.toFixed(1)} fps` : '—';
  els.info.bitrate.textContent = formatKbps(stats.kbps || status.bitrateKbps);
  els.info.buffer.textContent = stats.bufferMs ? `${Math.round(stats.bufferMs)} ms` : '—';
  els.info.viewers.textContent = status.viewers != null ? String(status.viewers) : '—';
  els.info.uptime.textContent = formatUptime(status.uptimeSeconds);

  els.chipFps.textContent = fps ? `${fps.toFixed(0)} fps` : '— fps';
  els.chipBitrate.textContent = formatKbps(stats.kbps || status.bitrateKbps);
  els.chipBuffer.textContent = stats.bufferMs ? `${Math.round(stats.bufferMs)} ms` : '— ms';
}

// ------------------------------------------------------------------- eventos

let offlineTimer = 0;

player.addEventListener('playing', () => {
  window.clearTimeout(offlineTimer);
  offlineTimer = 0;
  setState('playing');
  updateInfo();
  window.clearInterval(hudTimer);
  hudTimer = window.setInterval(updateInfo, 500);
});

player.addEventListener('fail', (event) => {
  window.clearInterval(hudTimer);
  setState('error', event.detail || 'Erro desconhecido na reprodução.');
  els.btnRetry.focus();
});

player.addEventListener('connected', () => {
  window.clearTimeout(offlineTimer);
  offlineTimer = 0;
  if (viewState === 'playing') els.chipBuffer.textContent = '— ms';
});

player.addEventListener('idle', (event) => {
  // Saiu de uma sessão (ou tentou entrar) sem transmissão ativa: volta a esperar.
  window.clearTimeout(offlineTimer);
  offlineTimer = 0;
  window.clearInterval(hudTimer);
  setState('idle', event.detail || 'Aguardando uma nova transmissão do computador.');
});

player.addEventListener('recovering', () => {
  if (viewState === 'playing') els.chipBuffer.textContent = 'sincronizando…';
});

player.addEventListener('disconnected', () => {
  if (viewState !== 'playing') return;

  // O vídeo continua no último quadro enquanto o WebSocket é reaberto.
  els.chipBuffer.textContent = 'reconectando…';
  els.linkDot.dataset.link = 'offline';

  // As tentativas de reconexão disparam 'disconnected' de novo: o prazo é do primeiro aviso.
  if (offlineTimer) return;

  offlineTimer = window.setTimeout(() => {
    offlineTimer = 0;
    if (viewState !== 'playing') return;
    setState('error', 'A conexão com o computador caiu. Verifique se o StreamBox continua aberto e tente novamente.');
    els.btnRetry.focus();
  }, 20000);
});

// Toque/clique na tela também abre as informações (útil com mouse e com controle).
els.screen.addEventListener('click', (event) => {
  if (viewState !== 'playing') return;
  if (event.target && event.target.closest && event.target.closest('button')) return;
  toggleOverlay();
});

els.btnPlay.addEventListener('click', () => {
  if (status && status.streaming) void attemptStart(true);
  else void poll();
});

els.btnRetry.addEventListener('click', () => { if (status && status.streaming) void attemptStart(true); else void poll(); });
els.btnCloseOverlay.addEventListener('click', closeOverlay);
els.btnDisconnect.addEventListener('click', disconnect);

document.addEventListener('keydown', (event) => {
  const key = event.key;

  if (els.overlay.hidden === false) {
    if (key === 'Escape' || key === 'Backspace' || key === 'BrowserBack' || key === 'GoBack') {
      event.preventDefault();
      closeOverlay();
      return;
    }
    if (key === 'Enter' || key === ' ' || key === 'MediaPlayPause') {
      event.preventDefault();
      const target = document.activeElement;
      if (target === els.btnDisconnect) disconnect();
      else closeOverlay();
      return;
    }
  }

  switch (key) {
    case 'Enter':
    case ' ':
    case 'MediaPlayPause':
      event.preventDefault();
      if (viewState === 'ready') void attemptStart(true);
      else if (viewState === 'playing') toggleOverlay();
      else if (viewState === 'error') void attemptStart(true);
      else if (viewState === 'idle') void poll();
      break;

    case 'Escape':
    case 'Backspace':
    case 'BrowserBack':
    case 'GoBack':
      event.preventDefault();
      if (viewState === 'playing' || viewState === 'connecting' || viewState === 'ready') disconnect();
      break;

    case 'ArrowUp':
    case 'i':
    case 'I':
      if (viewState === 'playing') { event.preventDefault(); toggleOverlay(); }
      break;

    default:
      break;
  }
});

// -------------------------------------------------------------------- start

setState('idle', 'Procurando o computador na rede local…');
void poll();
