/**
 * StreamPlayer — recebe vídeo H.264/AAC em MP4 fragmentado por WebSocket
 * e reproduz via Media Source Extensions, mantendo a latência baixa.
 */

const DEFAULT_CODECS = [
  'video/mp4; codecs="avc1.64002A,mp4a.40.2"',
  'video/mp4; codecs="avc1.4D402A,mp4a.40.2"',
  'video/mp4; codecs="avc1.42E02A,mp4a.40.2"',
  'video/mp4; codecs="avc1.640028,mp4a.40.2"',
];

const TARGET_LAG = 0.65;   // segundos de buffer desejados
const MAX_LAG = 2.2;       // acima disso, salta para o fim do buffer
const TRIM_KEEP = 4;       // segundos mantidos atrás do ponto de reprodução

export class StreamPlayer extends EventTarget {
  constructor(video) {
    super();
    this._video = video;
    this._ws = null;
    this._ms = null;
    this._sb = null;
    this._active = false;
    this._connected = false;

    this._buffer = new Uint8Array(0);
    this._offset = 0;
    this._initParts = [];
    this._haveInit = false;
    this._pending = [];
    this._codecs = DEFAULT_CODECS;
    this._codecIndex = 0;
    this._codec = '';

    this._bytesWindow = 0;
    this._statsAt = 0;
    this._kbps = 0;
    this._reconnectDelay = 1000;
    this._reconnectTimer = 0;
    this._tickTimer = 0;
    this._needClear = false;
    this._lastTime = -1;
    this._stalledSince = 0;
    this._reconnects = 0;
    this._recoveries = [];

    this._onTick = this._tick.bind(this);
    this._onPlaying = () => this.dispatchEvent(new Event('playing'));
    this._onVideoError = () => {
      const err = this._video.error;
      this._fail(err ? `Erro de mídia (${err.code})` : 'Erro de mídia');
    };

    this._video.addEventListener('playing', this._onPlaying);
    this._video.addEventListener('error', this._onVideoError);
  }

  get codec() { return this._codec; }
  get connected() { return this._connected; }

  get stats() {
    const v = this._video;
    let bufferMs = 0;
    if (v && v.buffered.length) {
      const end = v.buffered.end(v.buffered.length - 1);
      bufferMs = Math.max(0, (end - v.currentTime) * 1000);
    }
    return { kbps: this._kbps, bufferMs };
  }

  /** Inicia a reprodução. Requer gesto do usuário (política de autoplay). */
  async start(wsUrl, codecs) {
    this.stop();
    this._active = true;
    this._wsUrl = wsUrl;
    this._reconnectDelay = 1000;
    if (Array.isArray(codecs) && codecs.length) this._codecs = codecs;
    this._codecIndex = 0;

    const ok = this._setupMediaSource();
    if (!ok) {
      this._fail('Este navegador não suporta H.264 em MP4 fragmentado (MSE).');
      return;
    }

    // Conecta antes de pedir play(): com MSE ainda sem dados o play() fica pendente
    // e, se esperássemos por ele, o WebSocket nunca seria aberto.
    this._video.play().catch(() => { /* autoplay bloqueado: retomado no appendBuffer */ });
    this._connect();
    this._tickTimer = window.setInterval(this._onTick, 250);
  }

  stop() {
    this._active = false;
    this._connected = false;
    window.clearInterval(this._tickTimer);
    window.clearTimeout(this._reconnectTimer);
    this._tickTimer = 0;
    this._reconnectTimer = 0;

    if (this._ws) {
      try { this._ws.onclose = null; this._ws.close(); } catch (_) {}
      this._ws = null;
    }

    if (this._sb) {
      try { if (this._ms && this._ms.readyState === 'open') this._ms.removeSourceBuffer(this._sb); } catch (_) {}
      this._sb = null;
    }

    if (this._ms) {
      try {
        if (this._ms.readyState === 'open') this._ms.endOfStream();
      } catch (_) {}
      this._ms = null;
    }

    if (this._video) {
      try { this._video.pause(); } catch (_) {}
      this._video.removeAttribute('src');
      try { this._video.load(); } catch (_) {}
    }

    this._buffer = new Uint8Array(0);
    this._offset = 0;
    this._initParts = [];
    this._haveInit = false;
    this._pending = [];
    this._kbps = 0;
  }

  _fail(message) {
    this.stop();
    this.dispatchEvent(new CustomEvent('fail', { detail: message }));
  }

  // ---------------------------------------------------------------- MSE

  _setupMediaSource() {
    const video = this._video;
    if (!window.MediaSource) return false;

    while (this._codecIndex < this._codecs.length) {
      const candidate = this._codecs[this._codecIndex];
      if (MediaSource.isTypeSupported(candidate)) {
        this._codec = candidate;
        break;
      }
      this._codecIndex++;
    }

    if (!this._codec) return false;

    const ms = new MediaSource();
    this._ms = ms;
    video.src = URL.createObjectURL(ms);

    ms.addEventListener('sourceopen', () => {
      if (!this._active || this._ms !== ms) return;
      try {
        const sb = ms.addSourceBuffer(this._codec);
        sb.mode = 'segments';
        sb.addEventListener('updateend', () => {
          this._clearBuffer();
          this._syncTime();
          this._pump();
        });
        sb.addEventListener('error', () => this._recover('Falha no buffer de mídia'));
        this._sb = sb;
        this._pump();
      } catch (e) {
        this._fail('Falha ao criar o buffer de mídia: ' + e.message);
      }
    }, { once: true });

    return true;
  }

  // ------------------------------------------------------------ WebSocket

  _connect() {
    window.clearTimeout(this._reconnectTimer);
    if (!this._active) return;

    let ws;
    try {
      ws = new WebSocket(this._wsUrl);
    } catch (_) {
      this._scheduleReconnect();
      return;
    }

    ws.binaryType = 'arraybuffer';
    this._ws = ws;

    ws.onopen = () => {
      if (this._ws !== ws) return;
      this._connected = true;
      this._reconnectDelay = 1000;
      this.dispatchEvent(new Event('connected'));
    };

    ws.onmessage = (event) => {
      if (this._ws !== ws) return;
      if (typeof event.data === 'string') {
        this._onControl(event.data);
        return;
      }
      this._onChunk(new Uint8Array(event.data));
    };

    ws.onerror = () => { /* tratado no onclose */ };

    ws.onclose = () => {
      if (this._ws !== ws) return;
      this._connected = false;
      this._ws = null;
      if (!this._active) return;
      this._reconnects++;
      this.dispatchEvent(new CustomEvent('disconnected', { detail: { reconnects: this._reconnects } }));
      this._scheduleReconnect();
    };
  }

  _scheduleReconnect() {
    if (!this._active) return;
    window.clearTimeout(this._reconnectTimer);
    this._reconnectTimer = window.setTimeout(() => {
      if (!this._active) return;
      this._resetStreamState();
      this._connect();
    }, this._reconnectDelay);
    this._reconnectDelay = Math.min(this._reconnectDelay * 1.8, 5000);
  }

  /** Reinicia o decoder preservando a sessão (erro de mídia, buffer corrompido). */
  _recover(reason) {
    if (!this._active) return;

    // Se nem reiniciar resolve, insistir só piora: avisa o usuário e para.
    const now = performance.now();
    this._recoveries = this._recoveries.filter((t) => now - t < 60000);
    if (this._recoveries.length >= 3) {
      this._fail('A reprodução não se estabilizou. Inicie a transmissão novamente no computador.');
      return;
    }
    this._recoveries.push(now);

    this.dispatchEvent(new CustomEvent('recovering', { detail: reason }));

    const url = this._wsUrl;
    const codecs = this._codecs;
    this.stop();
    void this.start(url, codecs);
  }

  _onControl(text) {
    let message;
    try { message = JSON.parse(text); } catch (_) { return; }
    if (!message) return;

    if (message.type === 'error') {
      this._fail(message.detail || 'Servidor recusou a sessão.');
      return;
    }

    if (message.type === 'idle') {
      // O computador está ligado mas não transmitindo: mantém a sessão para tentar de novo.
      this.stop();
      this.dispatchEvent(new CustomEvent('idle', { detail: message.detail || '' }));
      return;
    }

    if (message.type === 'reset') {
      // O servidor descartou fragmentos (receptor lento): o alinhamento das caixas se perdeu.
      // Recomeça o parser e aceita o segmento inicial que vem em seguida, sem parar o vídeo.
      this._buffer = new Uint8Array(0);
      this._offset = 0;
      this._initParts = [];
      this._pending = [];
      this._haveInit = false;
    }
  }

  _resetStreamState() {
    this._buffer = new Uint8Array(0);
    this._offset = 0;
    this._initParts = [];
    this._haveInit = false;
    this._pending = [];
    this._needClear = true;
    this._clearBuffer();
  }

  _clearBuffer() {
    if (!this._needClear) return;

    const sb = this._sb;
    if (!sb || !sb.buffered.length) {
      this._needClear = false;
      return;
    }

    if (sb.updating) return; // tenta de novo no próximo updateend

    try {
      sb.remove(0, Infinity);
      this._needClear = false;
    } catch (_) {}
  }

  // --------------------------------------------------------- MP4 fMP4 parse

  _onChunk(chunk) {
    this._bytesWindow += chunk.length;

    // compacta o buffer anterior + novo bloco
    const remaining = this._buffer.length - this._offset;
    const merged = new Uint8Array(remaining + chunk.length);
    if (remaining > 0) merged.set(this._buffer.subarray(this._offset), 0);
    merged.set(chunk, remaining);
    this._buffer = merged;
    this._offset = 0;

    this._parse();

    // Um bloco que não fecha nenhuma caixa não pode crescer sem limite.
    if (this._buffer.length - this._offset > 4 * 1024 * 1024) {
      this._keepTail(256 * 1024);
    }
  }

  _keepTail(bytes) {
    const buf = this._buffer;
    const from = Math.max(this._offset, buf.length - bytes);
    this._buffer = buf.slice(from);
    this._offset = 0;
  }

  /**
   * Realinha o parser à próxima caixa MP4 conhecida. Acontece quando chegam bytes
   * fora de ordem/sem o início de uma caixa (ex.: fragmento descartado no caminho).
   */
  _resync() {
    const buf = this._buffer;
    const from = this._offset;
    const needles = ['moof', 'styp', 'ftyp', 'mdat'];
    let best = -1;

    for (let i = from + 4; i + 4 <= buf.length; i++) {
      const c0 = buf[i], c1 = buf[i + 1], c2 = buf[i + 2], c3 = buf[i + 3];
      if (!((c0 === 109 /* m */ && c1 === 111 /* o */ && c2 === 111 && c3 === 102 /* f */) ||
            (c0 === 115 /* s */ && c1 === 116 && c2 === 121 && c3 === 112 /* p */) ||
            (c0 === 102 /* f */ && c1 === 116 && c2 === 121 && c3 === 112) ||
            (c0 === 109 /* m */ && c1 === 100 && c2 === 97 /* a */ && c3 === 116 /* t */))) {
        continue;
      }

      const candidate = i - 4; // início do campo de tamanho
      if (candidate >= from && (best < 0 || candidate < best)) best = candidate;
      break;
    }

    if (best < 0) {
      this._keepTail(64 * 1024);
      return false;
    }

    this._offset = best;
    return true;
  }

  _parse() {
    for (;;) {
      const buf = this._buffer;
      const off = this._offset;
      if (buf.length - off < 8) return;

      const view = new DataView(buf.buffer, buf.byteOffset + off, buf.length - off);
      let size = view.getUint32(0);
      const type = String.fromCharCode(buf[off + 4], buf[off + 5], buf[off + 6], buf[off + 7]);
      let header = 8;

      if (size === 1) {
        if (buf.length - off < 16) return;
        size = Number(view.getBigUint64(8));
        header = 16;
      }

      if (size < header) {
        // Caixa inválida: tenta se realinhar à próxima caixa conhecida.
        if (!this._resync()) return;
        continue;
      }

      if (buf.length - off < size) return;

      if (type === 'ftyp' || type === 'moov') {
        this._initParts.push(buf.slice(off, off + size));
        this._offset = off + size;
        if (type === 'moov') this._commitInit();
        continue;
      }

      if (type === 'moof') {
        const afterMoof = off + size;
        const rest = buf.length - afterMoof;

        if (rest >= 8) {
          const nextSize = new DataView(buf.buffer, buf.byteOffset + afterMoof, rest).getUint32(0);
          const nextType = String.fromCharCode(
            buf[afterMoof + 4], buf[afterMoof + 5], buf[afterMoof + 6], buf[afterMoof + 7]);

          if (nextType === 'mdat') {
            if (rest < nextSize) return; // aguarda o restante do fragmento
            this._enqueue(buf.slice(off, afterMoof + nextSize));
            this._offset = afterMoof + nextSize;
            continue;
          }
        } else {
          return; // aguarda o cabeçalho do mdat
        }

        this._enqueue(buf.slice(off, afterMoof));
        this._offset = afterMoof;
        continue;
      }

      // styp, sidx, free, mfra... não são necessários para MSE
      this._offset = off + size;
    }
  }

  /**
   * Um receptor aberto no meio da transmissão recebe o init + fragmentos com timestamps
   * já avançados. Sem reposicionar, o vídeo fica esperando dados em currentTime = 0.
   */
  _syncTime() {
    const v = this._video;
    const sb = this._sb;
    if (!v || !sb || !sb.buffered.length) return;

    const start = sb.buffered.start(0);
    const end = sb.buffered.end(sb.buffered.length - 1);
    if (end <= start) return;

    if (v.currentTime < start - 0.2 || v.currentTime > end + 0.5) {
      const target = Math.max(start, end - TARGET_LAG);
      try { v.currentTime = target; } catch (_) {}
    }
  }

  _commitInit() {
    if (this._haveInit) return;
    this._haveInit = true;

    let total = 0;
    for (const part of this._initParts) total += part.length;
    const init = new Uint8Array(total);
    let at = 0;
    for (const part of this._initParts) { init.set(part, at); at += part.length; }
    this._initParts = [];

    this._pending.unshift(init);
    this.dispatchEvent(new Event('init'));
    this._pump();
  }

  _enqueue(segment) {
    if (!this._haveInit) {
      // fragmento chegou antes do init: guarda para depois
      this._pending.push(segment);
      return;
    }
    this._pending.push(segment);

    // Se o decodificador está atrás, descarta fragmento antigo em vez de crescer o atraso.
    if (this._pending.length > 60) {
      this._pending.splice(0, this._pending.length - 40);
    }
    this._pump();
  }

  _pump() {
    const sb = this._sb;
    if (!sb || sb.updating || !this._ms || this._ms.readyState !== 'open') return;
    if (!this._pending.length) return;

    const segment = this._pending.shift();
    try {
      sb.appendBuffer(segment);
      if (this._video.paused && this._active) {
        this._video.play().catch(() => {});
      }
    } catch (error) {
      if (error && error.name === 'QuotaExceededError') {
        this._pending.unshift(segment);
        this._trim(true);
      } else {
        this._fail('Falha ao enviar dados para o decodificador: ' + (error && error.message));
      }
    }
  }

  // ------------------------------------------------------------- latência

  _tick() {
    const video = this._video;
    if (!video || !this._active) return;

    const now = performance.now();
    if (this._statsAt) {
      const seconds = (now - this._statsAt) / 1000;
      if (seconds > 0) this._kbps = Math.round((this._bytesWindow * 8) / 1000 / seconds);
    }
    this._statsAt = now;
    this._bytesWindow = 0;

    if (this._needClear) this._clearBuffer();

    // Vigia de travamento: se o tempo não anda e há dados à frente, salta para o fim;
    // se não há dados nenhum, recomeça a conexão.
    if (video.currentTime !== this._lastTime) {
      this._lastTime = video.currentTime;
      this._stalledSince = 0;
    } else if (!video.paused && !video.seeking && this._connected) {
      if (!this._stalledSince) this._stalledSince = now;
      const stalledFor = now - this._stalledSince;

      if (stalledFor > 6000) {
        this._stalledSince = 0;
        if (video.buffered.length) this._recover('Reprodução travada');
        else this._scheduleReconnect();
      }
    }

    const buffered = video.buffered;
    if (!buffered.length) return;

    const end = buffered.end(buffered.length - 1);
    const lag = end - video.currentTime;

    if (lag > MAX_LAG) {
      video.currentTime = Math.max(0, end - TARGET_LAG);
      video.playbackRate = 1;
    } else if (lag > 1.1) {
      video.playbackRate = 1.06;
    } else if (lag < 0.28) {
      video.playbackRate = 0.96;
    } else {
      video.playbackRate = 1;
    }

    this._trim(false);
  }

  _trim(force) {
    const sb = this._sb;
    const video = this._video;
    if (!sb || sb.updating || !sb.buffered.length) return;
    const start = sb.buffered.start(0);
    const current = video.currentTime;
    if (force || (current - start > 12 && current > 14)) {
      const end = Math.max(start, current - TRIM_KEEP);
      try { sb.remove(start, end); } catch (_) {}
    }
  }
}
