// demo.js - the Wasm Demos' only script: it starts each demo module on the page and does nothing
// else. Everything a demo does (its simulation, its drawing, its colours, what the pointer means)
// is in the module, hand-written WebAssembly built by AsmSdk (samples/WasmDemos); a browser can
// only start WebAssembly from JavaScript and only let JavaScript touch a canvas, so this script
// loads the module, hands it the pointer, the wheel, the buttons and the theme, and copies the
// frame the module drew into the canvas. Every module exports the same interface (demo.map):
//   demo_init(width, height) -> frame address   demo_theme(dark)
//   demo_pointer(x, y, buttons)                  demo_wheel(x, y, delta)
//   demo_command(command)                        demo_frame() -> work done
// Each demo is a <div class="wasm-demo" data-module="life.wasm" data-unit="cells">, holding a
// canvas (its size is the frame's), buttons with data-command, and a .wasm-demo-numbers element
// for the live figures: the work per second the module reports, timed around demo_frame alone,
// on the page's one thread (one core), as AsmFoundation states every speed figure.

/**
 * Formats a rate in words: 13.3 billion, 92 million, 840,000.
 * @param {number} perSecond the rate.
 * @returns {string} the rate, rounded to three figures.
 */
function words(perSecond) {
  if (perSecond >= 1e9) return `${(perSecond / 1e9).toPrecision(3)} billion`;
  if (perSecond >= 1e6) return `${(perSecond / 1e6).toPrecision(3)} million`;
  return Math.round(perSecond).toLocaleString('en-US');
}

/**
 * Whether the docs show their dark theme: DocFX's modern template sets data-bs-theme on <html>,
 * from the reader's OS setting ("auto", this site's default) or from its theme switch.
 * @returns {boolean} true for dark.
 */
function isDark() {
  return document.documentElement.getAttribute('data-bs-theme') === 'dark';
}

/**
 * Loads a module: streaming when the server sends application/wasm, else from the bytes (some
 * local servers send another type, which instantiateStreaming refuses).
 * @param {string} url the .wasm file, relative to the page.
 * @returns {Promise<WebAssembly.Exports>} the module's exports (it imports nothing).
 * @throws {Error} the file cannot be fetched, or is not a valid module.
 */
async function load(url) {
  try {
    return (await WebAssembly.instantiateStreaming(fetch(url), {})).instance.exports;
  } catch {
    const response = await fetch(url);
    if (!response.ok) throw new Error(`${url}: ${response.status}`);
    return (await WebAssembly.instantiate(await response.arrayBuffer(), {})).instance.exports;
  }
}

/**
 * Starts one demo: instantiates its module, wires the canvas, the buttons and the theme to it,
 * and runs it, one demo_frame per animation frame, while the page is visible.
 * @param {HTMLElement} root the demo's .wasm-demo element.
 * @returns {Promise<void>} once it runs; a failure is shown in the numbers line, not thrown.
 */
async function start(root) {
  const canvas = root.querySelector('canvas');
  const numbers = root.querySelector('.wasm-demo-numbers');
  const unit = root.dataset.unit || 'steps';
  let m;
  try {
    m = await load(root.dataset.module);
  } catch (error) {
    numbers.textContent = `The demo could not start: ${error.message}`;
    return;
  }
  const w = canvas.width, h = canvas.height;
  const frame = m.demo_init(w, h);
  if (frame === 0) {
    numbers.textContent = `The demo refused a ${w} x ${h} frame.`;
    return;
  }
  m.demo_theme(isDark() ? 1 : 0);
  new MutationObserver(() => m.demo_theme(isDark() ? 1 : 0))
    .observe(document.documentElement, { attributes: true, attributeFilter: ['data-bs-theme'] });

  const context = canvas.getContext('2d');
  /**
   * Maps a pointer or wheel event to frame pixels: the canvas is scaled by CSS to the page's width.
   * @param {MouseEvent} event the event.
   * @returns {number[]} x and y in the frame.
   */
  const at = (event) => {
    const box = canvas.getBoundingClientRect();
    return [(event.clientX - box.left) * w / box.width, (event.clientY - box.top) * h / box.height];
  };
  for (const type of ['pointerdown', 'pointermove', 'pointerup']) {
    canvas.addEventListener(type, (event) => {
      if (type === 'pointerdown') canvas.setPointerCapture(event.pointerId);
      const [x, y] = at(event);
      m.demo_pointer(x, y, event.buttons);
    });
  }
  canvas.addEventListener('pointerleave', () => m.demo_pointer(-1, -1, 0));
  canvas.addEventListener('contextmenu', (event) => event.preventDefault());
  if (root.dataset.wheel !== undefined) {
    // Only the demos that zoom take the wheel from the page's scrolling.
    canvas.addEventListener('wheel', (event) => {
      event.preventDefault();
      const [x, y] = at(event);
      m.demo_wheel(x, y, event.deltaY);
    }, { passive: false });
  }
  for (const button of root.querySelectorAll('[data-command]')) {
    button.addEventListener('click', () => m.demo_command(Number(button.dataset.command)));
  }

  let work = 0, busy = 0, frames = 0, shown = performance.now(), last = '';
  /**
   * One animation frame: the module's step and drawing, timed; the frame copied to the canvas;
   * the figures refreshed twice a second.
   */
  const tick = () => {
    if (!document.hidden) {
      const before = performance.now();
      const done = m.demo_frame();
      busy += performance.now() - before;
      work += done;
      frames++;
      // The memory never grows, but a view must be made from the current buffer each time.
      context.putImageData(new ImageData(new Uint8ClampedArray(m.memory.buffer, frame, w * h * 4), w, h), 0, 0);
      const now = performance.now();
      if (now - shown > 500) {
        if (work > 0) {
          last = `One core: ${words(work / busy * 1000)} ${unit} a second, ${(busy / frames).toFixed(1)} ms a frame.`;
          numbers.textContent = last;
        } else if (last) {
          numbers.textContent = `${last} ${root.dataset.idle || 'Paused.'}`;
        }
        work = 0; busy = 0; frames = 0; shown = now;
      }
    }
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
}

for (const root of document.querySelectorAll('.wasm-demo')) start(root);
