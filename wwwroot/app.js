"use strict";

const board = document.querySelector("#board");
const ctx = board.getContext("2d", { alpha: false });
const viewport = document.querySelector("#viewport");
const grid = document.querySelector("#gridOverlay");
const gridCtx = grid.getContext("2d");
const coordsElement = document.querySelector("#coords");
const zoomElement = document.querySelector("#zoom");
const cooldownElement = document.querySelector("#cooldown");
const connectionElement = document.querySelector("#connection");
const showGrid = document.querySelector("#showGrid");

let pixels = null; // Raw RGB bytes, 3 per pixel.
let nextAllowedAt = 0;
let syncing = false;
let queuedChanges = [];
let placing = false;

function setConnection(label, live) {
    connectionElement.textContent = label;
    connectionElement.classList.toggle("live", live);
}
function setCooldownFromServer(data) {
    nextAllowedAt = Date.now() + Math.max(0, data.nextAllowedAtUnixMs - data.serverNowUnixMs);
    updateCountdown();
}
function updateCountdown() {
    const seconds = Math.max(0, Math.ceil((nextAllowedAt - Date.now()) / 1000));
    cooldownElement.textContent = seconds ? `${seconds}s` : "Ready!";
    cooldownElement.classList.toggle("ready", seconds === 0);
}
setInterval(updateCountdown, 250);

// ----- Full RGB picker: saturation/value, hue, hex, and R/G/B all stay in sync. -----
const colorField = document.querySelector("#colorField");
const colorHandle = document.querySelector("#colorHandle");
const hueInput = document.querySelector("#hue");
const hexInput = document.querySelector("#hex");
const nativeColor = document.querySelector("#nativeColor");
const preview = document.querySelector("#preview");
const channelInputs = ["#red", "#green", "#blue"].map(s => document.querySelector(s));
let hue = 0, saturation = 0, value = 0;
let selectedHex = "#D14C4C";

const toHex = ([r, g, b]) => "#" + [r, g, b].map(v => Math.round(v).toString(16).padStart(2, "0")).join("").toUpperCase();
function hsvToRgb(h, s, v) {
    const c = v * s, x = c * (1 - Math.abs((h / 60) % 2 - 1)), m = v - c;
    let rgb;
    if (h < 60) rgb = [c, x, 0];
    else if (h < 120) rgb = [x, c, 0];
    else if (h < 180) rgb = [0, c, x];
    else if (h < 240) rgb = [0, x, c];
    else if (h < 300) rgb = [x, 0, c];
    else rgb = [c, 0, x];
    return rgb.map(n => Math.round((n + m) * 255));
}
function rgbToHsv(r, g, b) {
    r /= 255; g /= 255; b /= 255;
    const max = Math.max(r, g, b), min = Math.min(r, g, b), delta = max - min;
    let h = 0;
    if (delta !== 0) {
        if (max === r) h = ((g - b) / delta) % 6;
        else if (max === g) h = (b - r) / delta + 2;
        else h = (r - g) / delta + 4;
        h = (h * 60 + 360) % 360;
    }
    return [h, max === 0 ? 0 : delta / max, max];
}
function refreshColorUi() {
    const rgb = hsvToRgb(hue, saturation, value);
    selectedHex = toHex(rgb);
    colorField.style.backgroundColor = `hsl(${hue}, 100%, 50%)`;
    colorHandle.style.left = `${saturation * 100}%`;
    colorHandle.style.top = `${(1 - value) * 100}%`;
    hueInput.value = String(Math.round(hue));
    preview.style.backgroundColor = selectedHex;
    nativeColor.value = selectedHex;
    hexInput.value = selectedHex;
    hexInput.classList.remove("invalid");
    channelInputs.forEach((input, i) => { input.value = String(rgb[i]); });
}
function setHex(hex) {
    if (!/^#[0-9a-f]{6}$/i.test(hex)) return false;
    const rgb = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));
    [hue, saturation, value] = rgbToHsv(...rgb);
    refreshColorUi();
    return true;
}
setHex(selectedHex);
let editingSv = false;
function setSvFromPointer(event) {
    const rect = colorField.getBoundingClientRect();
    saturation = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
    value = 1 - Math.max(0, Math.min(1, (event.clientY - rect.top) / rect.height));
    refreshColorUi();
}
colorField.addEventListener("pointerdown", event => {
    if (event.button !== 0) return;
    editingSv = true;
    colorField.setPointerCapture(event.pointerId);
    setSvFromPointer(event);
});
colorField.addEventListener("pointermove", event => { if (editingSv) setSvFromPointer(event); });
colorField.addEventListener("pointerup", () => { editingSv = false; });
colorField.addEventListener("pointercancel", () => { editingSv = false; });
hueInput.addEventListener("input", () => { hue = Number(hueInput.value) % 360; refreshColorUi(); });
hexInput.addEventListener("input", () => {
    if (!setHex(hexInput.value.trim())) hexInput.classList.add("invalid");
});
hexInput.addEventListener("blur", () => { hexInput.value = selectedHex; hexInput.classList.remove("invalid"); });
nativeColor.addEventListener("input", () => setHex(nativeColor.value));
for (const input of channelInputs) {
    input.addEventListener("change", () => {
        const numbers = channelInputs.map(field => Number(field.value));
        if (numbers.every(Number.isInteger) && numbers.every(n => n >= 0 && n <= 255)) setHex(toHex(numbers));
        else refreshColorUi();
    });
}

// ----- Zoom, pan, pixel rendering and optional crisp grid overlay. -----
let zoom = 1;
let offsetX = 0, offsetY = 0;
let pan = null;
function viewportSize() { return viewport.getBoundingClientRect().width; }
function clampOffsets() {
    const size = viewportSize();
    offsetX = Math.max(size * (1 - zoom), Math.min(0, offsetX));
    offsetY = Math.max(size * (1 - zoom), Math.min(0, offsetY));
}
function updateView() {
    clampOffsets();
    board.style.transform = `translate(${offsetX}px, ${offsetY}px) scale(${zoom})`;
    zoomElement.textContent = `${Math.round(zoom * 100)}%`;
    drawGrid();
}
function drawGrid() {
    const bounds = viewport.getBoundingClientRect();
    const width = bounds.width, height = bounds.height;
    if (!width || !height) return;
    const dpr = window.devicePixelRatio || 1;
    const w = Math.round(width * dpr), h = Math.round(height * dpr);
    if (grid.width !== w || grid.height !== h) { grid.width = w; grid.height = h; }
    gridCtx.setTransform(dpr, 0, 0, dpr, 0, 0);
    gridCtx.clearRect(0, 0, width, height);
    if (!showGrid.checked || !pixels) return;
    const cellX = width * zoom / board.width;
    const cellY = height * zoom / board.height;
    gridCtx.beginPath();
    for (let x = 0; x <= board.width; x++) {
        const px = offsetX + x * cellX;
        if (px < 0 || px > width) continue;
        const aligned = (Math.round(px * dpr) + .5) / dpr;
        gridCtx.moveTo(aligned, 0);
        gridCtx.lineTo(aligned, height);
    }
    for (let y = 0; y <= board.height; y++) {
        const py = offsetY + y * cellY;
        if (py < 0 || py > height) continue;
        const aligned = (Math.round(py * dpr) + .5) / dpr;
        gridCtx.moveTo(0, aligned);
        gridCtx.lineTo(width, aligned);
    }
    gridCtx.strokeStyle = "rgba(70, 78, 90, 0.19)";
    gridCtx.lineWidth = 1 / dpr;
    gridCtx.stroke();
}
showGrid.addEventListener("change", drawGrid);
new ResizeObserver(updateView).observe(viewport);
viewport.addEventListener("wheel", event => {
    event.preventDefault(); // Zoom canvas rather than the whole page while hovering.
    const rect = viewport.getBoundingClientRect();
    const mx = event.clientX - rect.left, my = event.clientY - rect.top;
    const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? rect.height : 1);
    const newZoom = Math.max(1, Math.min(24, zoom * Math.exp(-delta * 0.002)));
    const ratio = newZoom / zoom;
    offsetX = mx - (mx - offsetX) * ratio;
    offsetY = my - (my - offsetY) * ratio;
    zoom = newZoom;
    updateView();
}, { passive: false });
viewport.addEventListener("contextmenu", event => event.preventDefault());
function pixelFromPointer(event) {
    const rect = viewport.getBoundingClientRect();
    return {
        x: Math.floor((event.clientX - rect.left - offsetX) / (rect.width * zoom) * board.width),
        y: Math.floor((event.clientY - rect.top - offsetY) / (rect.height * zoom) * board.height)
    };
}
viewport.addEventListener("pointermove", event => {
    if (pan && event.pointerId === pan.pointerId) {
        offsetX = pan.originX + event.clientX - pan.startX;
        offsetY = pan.originY + event.clientY - pan.startY;
        updateView();
    }
    const { x, y } = pixelFromPointer(event);
    coordsElement.textContent = (x >= 0 && y >= 0 && x < board.width && y < board.height)
        ? `${x}, ${y}` : "—, —";
});
viewport.addEventListener("pointerleave", () => { if (!pan) coordsElement.textContent = "—, —"; });
viewport.addEventListener("pointerdown", event => {
    if (event.button === 1 || event.button === 2 || (event.shiftKey && event.button === 0)) {
        event.preventDefault();
        pan = { pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, originX: offsetX, originY: offsetY };
        viewport.setPointerCapture(event.pointerId);
        viewport.classList.add("panning");
    } else if (event.button === 0) {
        void placePixel(event);
    }
});
function endPan(event) {
    if (pan && pan.pointerId === event.pointerId) {
        pan = null;
        viewport.classList.remove("panning");
        if (viewport.hasPointerCapture(event.pointerId)) viewport.releasePointerCapture(event.pointerId);
    }
}
viewport.addEventListener("pointerup", endPan);
viewport.addEventListener("pointercancel", endPan);

function redraw() {
    if (!pixels) return;
    const image = ctx.createImageData(board.width, board.height);
    for (let i = 0; i < board.width * board.height; i++) {
        image.data[i * 4] = pixels[i * 3];
        image.data[i * 4 + 1] = pixels[i * 3 + 1];
        image.data[i * 4 + 2] = pixels[i * 3 + 2];
        image.data[i * 4 + 3] = 255;
    }
    ctx.putImageData(image, 0, 0);
    drawGrid();
}
function applyPixel(x, y, color) {
    if (!pixels || !/^#[0-9a-f]{6}$/i.test(color) || x < 0 || x >= board.width || y < 0 || y >= board.height) return;
    const i = (y * board.width + x) * 3;
    [pixels[i], pixels[i + 1], pixels[i + 2]] = [1, 3, 5].map(j => parseInt(color.slice(j, j + 2), 16));
    ctx.fillStyle = color;
    ctx.fillRect(x, y, 1, 1);
}
async function refreshState() {
    syncing = true;
    queuedChanges = [];
    try {
        const response = await fetch("/api/state", { cache: "no-store", credentials: "same-origin" });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const data = await response.json();
        board.width = data.width;
        board.height = data.height;
        pixels = Uint8Array.from(atob(data.pixels), c => c.charCodeAt(0));
        if (pixels.length !== board.width * board.height * 3) throw new Error("Invalid pixel data");
        redraw();
        setCooldownFromServer(data);
    } finally {
        for (const [x, y, color] of queuedChanges) applyPixel(x, y, color);
        syncing = false;
        queuedChanges = [];
    }
}
async function placePixel(event) {
    if (!pixels || placing || Date.now() < nextAllowedAt) return;
    const { x, y } = pixelFromPointer(event);
    if (x < 0 || x >= board.width || y < 0 || y >= board.height) return;
    placing = true;
    const color = selectedHex;
    try {
        const response = await fetch("/api/pixel", {
            method: "POST", credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ x, y, color })
        });
        const data = await response.json();
        if (response.status === 429) setCooldownFromServer(data);
        else if (response.ok) {
            setCooldownFromServer(data);
            applyPixel(x, y, color);
        } else console.error("Pixel rejected:", data.error || response.status);
    } catch (error) {
        console.error("Unable to place pixel:", error);
        setConnection("Connection lost", false);
    } finally { placing = false; }
}

async function startRealtime() {
    if (!window.signalR) { setConnection("SignalR unavailable", false); return; }
    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hub").withAutomaticReconnect().build();
    connection.on("PixelChanged", (x, y, color) => {
        if (syncing) queuedChanges.push([x, y, color]);
        else applyPixel(x, y, color);
    });
    connection.onreconnecting(() => setConnection("Reconnecting…", false));
    connection.onreconnected(async () => {
        setConnection("Live", true);
        try { await refreshState(); } catch (error) { console.error(error); }
    });
    connection.onclose(() => { setConnection("Disconnected", false); setTimeout(connect, 5000); });
    async function connect() {
        try {
            await connection.start();
            setConnection("Live", true);
            await refreshState(); // Cover the gap between initial snapshot and connection.
        } catch (error) {
            setConnection("Retrying…", false);
            console.error(error);
            setTimeout(connect, 5000);
        }
    }
    await connect();
}
(async () => {
    try {
        await refreshState();
        void startRealtime();
    } catch (error) {
        setConnection("Server unavailable", false);
        cooldownElement.textContent = "Offline";
        console.error(error);
    }
})();
