// 首頁背景的水波：滑鼠掃過去，那一點滴一滴，波紋擴散時折射底下的截圖、稜線上帶一點高光。
//
// 做法：高度場的波動方程（Hugo Elias 的水面演算法）在一張低解析度的浮點貼圖上跑，
// 每幀一步：h' = (2h − h₋₁ + c·∇²h)·damping。畫面那一步拿相鄰四點的高度差當法線，
// 把背景圖的取樣座標往法線方向推一點（折射），再用法線對光的夾角加高光。
// 純 WebGL，沒有函式庫；拿不到浮點貼圖（很舊的機器）就什麼都不做，維持靜態背景。
(() => {
  const hero = document.querySelector('.hero');
  const canvas = document.getElementById('ripple');
  if (!hero || !canvas) return;
  if (matchMedia('(prefers-reduced-motion: reduce)').matches) return;

  // ---- 建 context 與浮點貼圖能力 ----
  let gl = canvas.getContext('webgl2', { alpha: false, antialias: false, premultipliedAlpha: false });
  let floatType, internal, linearOk;
  if (gl) {
    if (!gl.getExtension('EXT_color_buffer_float')) gl = null;
    else { floatType = gl.HALF_FLOAT; internal = gl.RGBA16F; linearOk = true; }
  }
  if (!gl) {
    gl = canvas.getContext('webgl', { alpha: false, antialias: false, premultipliedAlpha: false });
    if (!gl) return;
    const hf = gl.getExtension('OES_texture_half_float');
    if (!hf || !gl.getExtension('EXT_color_buffer_half_float')) return;
    floatType = hf.HALF_FLOAT_OES; internal = gl.RGBA;
    linearOk = !!gl.getExtension('OES_texture_half_float_linear');
  }

  const VERT = `
attribute vec2 a;
varying vec2 v;
void main() { v = a * 0.5 + 0.5; gl_Position = vec4(a, 0.0, 1.0); }`;

  // 一步模擬：r = 目前高度、g = 上一幀高度
  const SIM = `
precision highp float;
varying vec2 v;
uniform sampler2D u_h;
uniform vec2 u_px;      // 1 / 模擬貼圖尺寸
uniform float u_damp;
void main() {
  vec2 c = texture2D(u_h, v).rg;
  float n = texture2D(u_h, v + vec2(0.0,  u_px.y)).r
          + texture2D(u_h, v - vec2(0.0,  u_px.y)).r
          + texture2D(u_h, v + vec2(u_px.x, 0.0)).r
          + texture2D(u_h, v - vec2(u_px.x, 0.0)).r;
  float h = (2.0 * c.r - c.g + 0.5 * (n - 4.0 * c.r)) * u_damp;
  // 邊緣只留一圈很窄的輕微吸收，讓撞到邊的波少反彈一點；帶太寬會讓滑到旁邊的波直接消失
  // （使用者 2026-09-06：「滑到旁邊會中斷」）
  float e = min(min(v.x, 1.0 - v.x), min(v.y, 1.0 - v.y));
  h *= mix(0.94, 1.0, smoothstep(0.0, 0.012, e));
  gl_FragColor = vec4(h, c.r, 0.0, 1.0);
}`;

  // 滴一滴：往高度場加一個高斯凹陷
  const SPLAT = `
precision highp float;
varying vec2 v;
uniform sampler2D u_h;
uniform vec2 u_at;      // 0..1
uniform float u_r;      // 半徑（uv）
uniform float u_amp;
uniform float u_aspect; // 寬/高，圓才不會變橢圓
void main() {
  vec2 c = texture2D(u_h, v).rg;
  vec2 d = (v - u_at) * vec2(u_aspect, 1.0);
  float g = exp(-dot(d, d) / (u_r * u_r));
  gl_FragColor = vec4(c.r - u_amp * g, c.g, 0.0, 1.0);
}`;

  // 畫面：用高度場折射背景圖，加高光
  const DRAW = `
precision highp float;
varying vec2 v;
uniform sampler2D u_h;
uniform sampler2D u_bg;
uniform vec2 u_px;
uniform vec2 u_cover;   // 背景圖 cover 的縮放
uniform vec2 u_offset;  // 背景圖 cover 的位移
uniform float u_fade;   // 進場淡入
void main() {
  float l = texture2D(u_h, v - vec2(u_px.x, 0.0)).r;
  float r = texture2D(u_h, v + vec2(u_px.x, 0.0)).r;
  float d = texture2D(u_h, v - vec2(0.0, u_px.y)).r;
  float u = texture2D(u_h, v + vec2(0.0, u_px.y)).r;
  vec2 grad = vec2(r - l, u - d);
  vec3 n = normalize(vec3(-grad * 6.0, 1.0));
  vec2 uv = v * u_cover + u_offset;
  uv.y = 1.0 - uv.y;
  vec3 col = texture2D(u_bg, uv + grad * 0.35).rgb;
  // 高光：光從左上斜射下來；只留很細的一條，波紋才像玻璃而不是塑膠
  vec3 light = normalize(vec3(-0.45, 0.6, 0.8));
  float spec = pow(max(dot(n, light), 0.0), 60.0);
  col += spec * 0.22;
  // 波谷稍微暗一點，波才有厚度
  col *= 1.0 + (r - l + u - d) * 0.4;
  gl_FragColor = vec4(col * u_fade, 1.0);
}`;

  const compile = (type, src) => {
    const sh = gl.createShader(type);
    gl.shaderSource(sh, src); gl.compileShader(sh);
    if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(sh));
    return sh;
  };
  const program = frag => {
    const p = gl.createProgram();
    gl.attachShader(p, compile(gl.VERTEX_SHADER, VERT));
    gl.attachShader(p, compile(gl.FRAGMENT_SHADER, frag));
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(p));
    const u = {};
    const n = gl.getProgramParameter(p, gl.ACTIVE_UNIFORMS);
    for (let i = 0; i < n; i++) { const info = gl.getActiveUniform(p, i); u[info.name] = gl.getUniformLocation(p, info.name); }
    return { p, u };
  };

  let sim, splat, draw;
  try { sim = program(SIM); splat = program(SPLAT); draw = program(DRAW); } catch { return; }

  const quad = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, quad);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
  for (const { p } of [sim, splat, draw]) {
    const a = gl.getAttribLocation(p, 'a');
    gl.useProgram(p); gl.enableVertexAttribArray(a); gl.vertexAttribPointer(a, 2, gl.FLOAT, false, 0, 0);
  }

  // ---- 模擬用的兩張貼圖（乒乓） ----
  let simW = 0, simH = 0, ping = [], pong = 0;
  const makeTarget = (w, h) => {
    const tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, tex);
    const f = linearOk ? gl.LINEAR : gl.NEAREST;
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, f);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, f);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texImage2D(gl.TEXTURE_2D, 0, internal, w, h, 0, gl.RGBA, floatType, null);
    const fb = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
    const ok = gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE;
    gl.clearColor(0, 0, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    return ok ? { tex, fb } : null;
  };

  // ---- 背景圖 ----
  const bgTex = gl.createTexture();
  let bgW = 0, bgH = 0, bgReady = false;
  const img = new Image();
  img.onload = () => {
    bgW = img.naturalWidth; bgH = img.naturalHeight;
    gl.bindTexture(gl.TEXTURE_2D, bgTex);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, img);
    bgReady = true;
    fadeStart = performance.now();
    // 先畫出第一幀再把 CSS 那份靜態背景換掉：分頁在背景時 rAF 不跑，不然會黑一片
    render(fadeStart);
    hero.classList.add('gl');
  };
  img.src = 'hero-bg.webp';

  // ---- 尺寸 ----
  let cssW = 0, cssH = 0;
  const resize = () => {
    const r = hero.getBoundingClientRect();
    const dpr = Math.min(devicePixelRatio || 1, 2);
    cssW = r.width; cssH = r.height;
    const w = Math.max(1, Math.round(r.width * dpr)), h = Math.max(1, Math.round(r.height * dpr));
    if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
    // 模擬解析度：長邊 512 就夠細（畫面那步是雙線性放大）
    const k = 512 / Math.max(r.width, r.height);
    const sw = Math.max(64, Math.round(r.width * k)), sh = Math.max(64, Math.round(r.height * k));
    if (sw !== simW || sh !== simH) {
      simW = sw; simH = sh;
      ping = [makeTarget(sw, sh), makeTarget(sw, sh)];
      if (!ping[0] || !ping[1]) { ok = false; }
    }
  };
  let ok = true;
  resize();
  if (!ok) return;
  addEventListener('resize', resize, { passive: true });

  // ---- 滴水 ----
  const drops = [];
  let lastX = NaN, lastY = NaN, lastT = 0;
  hero.addEventListener('pointermove', e => {
    const r = hero.getBoundingClientRect();
    const x = (e.clientX - r.left) / r.width, y = 1 - (e.clientY - r.top) / r.height;
    const now = performance.now();
    if (!isNaN(lastX)) {
      const dx = (x - lastX) * r.width, dy = (y - lastY) * r.height;
      const dist = Math.hypot(dx, dy);
      if (dist < 6) return;                          // 沒怎麼動就不滴，滴太密會變一片糊
      const speed = Math.min(dist / Math.max(now - lastT, 1), 3);
      // 慢慢滑＝細細一條；快甩＝大一點但有上限，不會爆
      drops.push({ x, y, r: 0.012 + speed * 0.004, amp: 0.10 + speed * 0.06 });
    }
    lastX = x; lastY = y; lastT = now;
  }, { passive: true });
  hero.addEventListener('pointerleave', () => { lastX = lastY = NaN; });

  // 沒人動的時候偶爾自己滴一下，水面才像活的（很小、很少）
  let nextIdle = performance.now() + 2500;

  // ---- 每幀 ----
  const bind = (prog, texUnitTex) => {
    gl.useProgram(prog.p);
    gl.bindBuffer(gl.ARRAY_BUFFER, quad);
    const a = gl.getAttribLocation(prog.p, 'a');
    gl.enableVertexAttribArray(a); gl.vertexAttribPointer(a, 2, gl.FLOAT, false, 0, 0);
    gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, texUnitTex);
    gl.uniform1i(prog.u.u_h, 0);
  };
  const step = (prog, setup) => {
    const src = ping[pong], dst = ping[1 - pong];
    gl.bindFramebuffer(gl.FRAMEBUFFER, dst.fb);
    gl.viewport(0, 0, simW, simH);
    bind(prog, src.tex);
    setup();
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    pong = 1 - pong;
  };

  let visible = true, fadeStart = 0, raf = 0;
  const io = new IntersectionObserver(es => { visible = es[0].isIntersecting; if (visible && !raf) raf = requestAnimationFrame(frame); });
  io.observe(hero);
  document.addEventListener('visibilitychange', () => { if (!document.hidden && visible && !raf) raf = requestAnimationFrame(frame); });

  function frame(now) {
    raf = 0;
    if (!visible || document.hidden) return;
    raf = requestAnimationFrame(frame);
    if (!bgReady) return;
    render(now);
  }
  canvas.__render = render; // 除錯用：分頁在背景時 rAF 不跑，可以手動推一幀

  function render(now) {

    if (now > nextIdle) {
      drops.push({ x: 0.15 + Math.random() * 0.7, y: 0.15 + Math.random() * 0.7, r: 0.01, amp: 0.05 });
      nextIdle = now + 3500 + Math.random() * 4000;
    }

    const aspect = simW / simH;
    for (let i = 0; i < 6 && drops.length; i++) {
      const d = drops.shift();
      step(splat, () => {
        gl.uniform2f(splat.u.u_at, d.x, d.y);
        gl.uniform1f(splat.u.u_r, d.r);
        gl.uniform1f(splat.u.u_amp, d.amp);
        gl.uniform1f(splat.u.u_aspect, aspect);
      });
    }
    drops.length = Math.min(drops.length, 12);

    step(sim, () => {
      gl.uniform2f(sim.u.u_px, 1 / simW, 1 / simH);
      gl.uniform1f(sim.u.u_damp, 0.985);
    });

    // 畫面
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    gl.viewport(0, 0, canvas.width, canvas.height);
    bind(draw, ping[pong].tex);
    gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, bgTex);
    gl.uniform1i(draw.u.u_bg, 1);
    gl.uniform2f(draw.u.u_px, 1 / simW, 1 / simH);
    // cover：跟 CSS background-size: cover 一樣，短邊貼齊、長邊裁中間
    const sc = Math.max(cssW / bgW, cssH / bgH);
    const cw = cssW / (bgW * sc), ch = cssH / (bgH * sc);
    gl.uniform2f(draw.u.u_cover, cw, ch);
    gl.uniform2f(draw.u.u_offset, (1 - cw) / 2, (1 - ch) / 2);
    gl.uniform1f(draw.u.u_fade, Math.min((now - fadeStart) / 1600, 1));
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
  }
  raf = requestAnimationFrame(frame);
})();
